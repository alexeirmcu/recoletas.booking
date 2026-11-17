# Design: Appointment Booking Service

## Technical Approach

Greenfield .NET 10 (`net10.0`; build with SDK 10 or later) service built with **Clean Architecture and CQRS** across five projects. Dependencies point inward:

```
API → ApplicationServices → Domain
API → Infrastructure → ApplicationServices
Infrastructure → Domain
ApplicationServices → Contracts
Infrastructure → Contracts
```

- `Contracts` owns integration events plus public request/response DTOs (zero deps), including
  `BookingResult` and `WaitlistResult`.
- `Domain` owns entities, value objects, the slot state machine, and domain-event records; it has no
  project or package dependencies.
- `ApplicationServices` owns CQRS commands/handlers/validators, use-case ports, centralized
  API/business error codes, background workers, typed integration-event handling, and
  domain-to-integration event mapping. It
  references Domain and Contracts, never Infrastructure.
- `Infrastructure` implements application ports and owns EF Core + PostgreSQL repositories, the
  outbox store, notification senders, event serialization, and the concrete dispatcher adapter.
- `API` is a thin controller layer and the composition root; it explicitly references
  ApplicationServices and Infrastructure.

The design hinges on three **swappable seams** that isolate every infrastructure decision:

| Seam | Today | Future (1 DI line to swap) |
|------|-------|-----------------------------|
| `IEventDispatcher` (`ApplicationServices/Messaging`) (transport) | Infrastructure `RabbitMqDispatcher` with publisher confirms | Separate Worker host (ADR-0001) |
| `INotificationSender` (`ApplicationServices/Messaging`) (delivery) | Infrastructure Log + Webhook via `CompositeNotificationSender` | Sms/Email/Push/Twilio |
| `ISlotRepository` (`ApplicationServices/Abstractions`) (persistence) | Infrastructure `SlotRepository` (EF Core + PostgreSQL) | Read-replica / multi-instance (hospital network) |

The API composition root integrates Serilog.AspNetCore with `Microsoft.Extensions.Logging`, so the
existing `ILogger<T>` calls share the same sinks. The minimum level is Information with Microsoft
framework logs overridden to Warning. Console output is enabled in every environment; Development
also writes `logs/recoletas-.log` beneath the API content root, rolling daily, retaining 14 files, and
rolling at 10 MiB. Production file logging is disabled; production should use stdout/central collection
or explicitly mount a volume if file output is enabled operationally. Package versions are
Serilog.AspNetCore 10.0.0, Serilog.Sinks.Console 6.1.1, and Serilog.Sinks.File 7.0.0.

Startup database work belongs to `Infrastructure/EfModel/BookingDatabaseInitializer`: it applies EF
migrations and optionally seeds the three synthetic demo slots. The API passes the opt-in flag only
when the environment is Development and `Booking:SeedDemoData` is true. The initializer uses
`IClock` and checks each exact test-type/start/end tuple, making repeated seeding safe without
blocking insertion of missing demo rows.

After migrations and before optional demo slots, the initializer idempotently inserts the supported
test-type codes into `TestTypes` in every environment. `TestTypes.Code` is the catalog key; its only
rows are `BloodTest`, `Ultrasound`, and `MRI`. Slots and waitlist entries use the existing `TestType`
value object with a string converter and restrictive FKs to that key. Application validators reject
unsupported `QueryAgenda` filters and `JoinWaitlist` requests; database FKs protect direct persistence.

`Patient.DeletedAtUtc` is nullable logical-deletion metadata, not an endpoint or physical-delete
operation. Restrictive patient FKs preserve references from assigned slots, reservations, and
waitlist entries; reservations also restrictively reference their slot. `Slots.CurrentReservationId`
keeps its unique filtered index but intentionally has no FK to `Reservations.Id`, avoiding a cycle
with `Reservations.SlotId → Slots.Id`.

Patient NHC remains optional. The Patient invariant trims supplied values and maps whitespace-only
values to null; PostgreSQL enforces uniqueness only for non-null values through `IX_Patients_Nhc`, a
filtered unique index. The repository translates only a PostgreSQL unique violation naming that index
to a Domain persistence exception, and the registration handler maps it to an application conflict
that the API returns as HTTP 409 with `patient_nhc_already_exists`. The additive migration detects
normalized duplicates and fails with patient IDs before changing data or creating the index; it never
deletes or merges patients.

Slot exclusivity is enforced in the database, never in memory. Messaging uses a transactional outbox
(state change + event INSERT in one PostgreSQL transaction). A successful direct hold emits
`HoldConfirmationRequested`; the outbox polling worker publishes to RabbitMQ and marks processed
only after confirmation. The notification consumer delivers only while the held slot still matches
the event's patient and generation and has not expired. Expiry uses a polling worker plus lazy
validity checks. Publisher and consumers remain hosted in the API. See ADR-0001 for the accepted topology.

## Architecture Decisions

| Decision | Options | Tradeoff | Chosen |
|----------|---------|----------|--------|
| Persistence | PostgreSQL vs SQLite vs Redis/in-memory | SQLite is single-writer/embedded; Redis adds a service; in-memory loses state | **PostgreSQL** (Npgsql + docker compose) |
| Concurrency | Conditional UPDATE vs app mutex | Mutexes fail across processes | **Conditional transactional UPDATE guarded by state, expiry, and generation** |
| Async messaging | Outbox + broker vs direct publish vs in-process channel | Broker adds an operational dependency; direct publish breaks atomicity; channel is volatile | **Transactional outbox + RabbitMQ confirmed publisher + hosted consumers** (at-least-once) |
| Expiry | Polling worker vs in-process timer vs lazy only | Timers lost on restart; lazy-only stalls promotion | **Polling worker + lazy validity checks** |
| Offers | Exclusive expiring lease vs non-exclusive notify | Non-exclusive races to accept and breaks FIFO | **Exclusive offer lease with generation counter** |
| DB access | EF Core vs Dapper | EF migrations fluent; Dapper for raw conditional SQL | **EF Core** + `ExecuteUpdateAsync` for conditional transitions |
| Layering | Clean Architecture vs linear dependencies | Inner layers must not depend on adapters | **Clean Architecture**: API composes ApplicationServices and Infrastructure; Infrastructure implements application ports |
| API style | Controllers + MediatR vs Minimal API | Minimal API scatters routing; controllers match team convention | **Controllers + MediatR (CQRS)** |
| Patient retention | Physical delete vs logical metadata + restrictive refs | Physical delete can orphan appointment and waitlist history | **Nullable `DeletedAtUtc`, no delete operation; restrictive FKs** |
| Patient NHC | App-only duplicate check vs PostgreSQL uniqueness | App-only checks race across requests and instances | **Optional trimmed NHC plus filtered unique index for non-null values** |
| Test types | Free-form strings vs persisted catalog | Free-form values drift and make persistence validation impossible | **Closed `TestTypes` catalog: BloodTest, Ultrasound, MRI** |

## Patient Pending Confirmations

The future patient website can discover current actionable holds and waitlist offers through
`GET /appointments/pending`. It uses only the authenticated patient's JWT identity and returns
an unpaginated array of `PendingAppointmentSummary` Contracts records: slot ID, test type, UTC
slot start/end, kind (`hold`/`offer`), current generation, and UTC lease expiry. No patient/NHC,
notification payload, outbox ID, or not-yet-created reservation ID is returned.

The existing slot repository port provides a patient/time-scoped read. Infrastructure filters in
PostgreSQL for AssignedPatientId, Held/Offered state, the corresponding non-null expiry > now,
and EndsAtUtc > now. The application captures the injectable UTC clock once. Results use earliest
expiry first, followed by slot start and slot ID. There are no N+1 reads or full agenda scans.

The thin controller reuses patient fallback authorization (401 invalid/missing credentials,
403 for admin access) and sends a MediatR query. Empty results return 200 with `[]`; the response
uses `Cache-Control: no-store`. The query does not mutate state or require a broker delivery.
No schema, migration, notification, or composition-root change is needed.

For a `hold`, submit the returned generation to `POST /holds/{slotId}/confirm`; for an `offer`,
use `POST /offers/{slotId}/accept`. A pending response is not a new lease or a guarantee: the
existing guarded transition can still return 409 if the item changes or expires before the POST.
The frontend must refresh after actions/conflicts. `GET /appointments` remains confirmed-only.

## Data Flow

Direct hold → asynchronous confirmation request:

```
API ── BlockSlot ──▶ ISlotRepository (conditional UPDATE AVAILABLE→HELD)
                         └── same unit of work: INSERT HoldConfirmationRequested outbox event
OutboxProcessorHostedService ──▶ RabbitMqDispatcher ──▶ booking.events (publisher confirms)
    └── mark processed only after confirmation             └──▶ booking.notifications
NotificationEventHandler (verify HELD + patient + generation + unexpired) ──▶ INotificationSender
```

NotificationEventHandler belongs to ApplicationServices. Infrastructure receives and deserializes
the delivery before delegating to that typed handler; it retains ACK/retry/DLQ responsibility.

Waitlist offer (complex flow):

```
SlotReleased outbox ──▶ RabbitMQ booking.workflow ──▶ typed application SlotReleased handler
    └──▶ OfferNextWaiter handler (event's expected generation)
   │  Exclusively claim earliest eligible waitlist entry
   │  Conditional UPDATE slot AVAILABLE→OFFERED using expected generation
   │  INSERT OfferCreated outbox event
   │  Commit together; failed slot transition must not commit the entry claim
   ▼
OutboxProcessor ──▶ RabbitMQ booking.notifications ──▶ NotificationEventHandler ──▶ INotificationSender
   │
AcceptOffer (conditional: state=OFFERED AND generation=@g AND offer_expires_at>now)
   └──▶ CONFIRMED        DeclineOffer / timeout ──▶ expire, advance FIFO, re-offer
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `src/Recoletas.Booking.Contracts/` | Create | Integration events + public request/response models (zero deps) |
| `src/Recoletas.Booking.Domain/` | Create | Entities, value objects, state machine, ports |
| `src/Recoletas.Booking.Infrastructure/` | Create | EF Core + PostgreSQL repos, startup initializer, outbox store, notifiers, clock |
| `src/Recoletas.Booking.ApplicationServices/` | Create | CQRS commands/handlers/validators, workers, dispatcher |
| `src/Recoletas.Booking.API/` | Create | Controllers, auth seam, Swagger, composition root |
| `tests/Recoletas.Booking.Tests/` | Create | MSTest + Moq (unit + integration + E2E) |
| `docker-compose.yml` | Create | PostgreSQL + pgAdmin |
| `README.md`, `AGENTS.md`, `doc/` | Create | Startup, decisions a–e, SAD, ADRs |

Public response records (`BookingResult`, `WaitlistResult`) are Contracts models alongside the other
HTTP DTOs; `ApplicationServices/Errors/BookingErrorCodes.cs` is the source for stable API/business
error-code values consumed by handlers and the API.

## Interfaces / Contracts

Application-owned ports live in `Recoletas.Booking.ApplicationServices.Abstractions`. Slot writes
use conditional transitions; event-driven offers carry the expected release generation. Waitlist
claims participate in the same unit of work as the slot and outbox writes.

Outbox leases carry an acquisition identity. Completion and failure updates must match the current,
unexpired acquisition; a stale owner cannot overwrite another worker's state. Broker publication
still requires confirmation, and lease fencing does not provide exactly-once publication.

Wire event identifiers are explicit and versioned, independent of CLR type renames. Only the
versioned identifiers are supported. Unknown identifiers fail explicitly rather than being silently
acknowledged.

`IEventDispatcher` and `INotificationSender` are application-owned ports in
`Recoletas.Booking.ApplicationServices.Messaging`; Infrastructure provides their implementations.
Domain event records do not depend on Contracts. ApplicationServices maps them to primitive Contracts
events such as `HoldConfirmationRequested` before enqueueing. Successful outbox and notification logs
correlate by structured event ID/type; simulated notification logs include a safe delivery outcome.
Outbox failure logs include only the exception type and retry/dead-letter disposition; polling-loop
failures are logged separately by the hosted worker.

Slot state machine: `AVAILABLE → HELD → CONFIRMED → AVAILABLE` and `AVAILABLE → OFFERED → CONFIRMED/AVAILABLE`.
Domain enforces: no double-booking, hold expiry, offer window, generation counter. Domain event records
are mapped to primitive integration event records in `Contracts/Events/`; no `IDomainEvent` marker is used.

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | Slot state machine transitions, expiry rules, offer acceptance | MSTest + `FakeClock` |
| Integration | Concurrent claims never double-assign; outbox atomicity; worker retry/dedup | PostgreSQL test DB, parallel `TryHoldAsync` |
| E2E | API contract via Swagger; full hold→confirm and waitlist→offer→accept | `WebApplicationFactory` + test host |

PostgreSQL integration and E2E tests use a dedicated `recoletas_tests` database by default, deriving
connection details from `ConnectionStrings__Booking` while overriding only the database name. An
explicit `ConnectionStrings__BookingTests` may select another dedicated test database. The helper
creates the database if absent and serializes migrations; test setup never targets or deletes the
application database. A test that requires an empty waitlist must isolate that population rather
than assume the retained shared database contains no earlier entries. Only explicitly identified
test-owned fixtures may be removed; unrelated rows must be preserved. Only recognized PostgreSQL unavailability may make these tests
inconclusive; schema, migration, authentication, and application failures are test failures. Test
hosts configure connections per factory rather than mutating process-wide environment variables.
Concurrency regression coverage includes competing slots consuming one waitlist entry, a stale
event generation, and outbox workers attempting to update a lease acquired by another worker.

The FIFO repository, waitlist E2E, and outbox-lease scenarios use fresh per-scenario databases named
`bookingtest_waitlistfifo_<guid>`, `bookingtest_waitliste2e_<guid>`, and
`bookingtest_outboxlease_<guid>`. They inherit configured server/credentials but override the shared
test database name. Each is migrated and catalog-seeded through the same helper and retained for
inspection. Repeating the suite must pass without resetting the shared database; the tradeoff is
that these scenario databases accumulate until explicitly removed by an operator.

Pending-state database tests and patient flow tests likewise use dedicated `pendingread`,
`pendingflow`, and `holdconfirm` scenario databases, preserving the application's and shared test
database state across repeated runs.

## Migration / Rollout

The repository keeps the existing baseline `InitialBooking` migration and additive migrations with
a matching model snapshot. The baseline creates
the current schema, including patient lifecycle metadata, the closed test-type catalog and restrictive
foreign keys, and the filtered unique NHC index. The initializer seeds catalog rows after migration.
This baseline is intended for a fresh database; replacing migration history does not upgrade a
database created from the previous migration IDs. Existing deployments require a separately planned
schema/data migration; an additive lease-ownership migration is not a replacement for that work.
Do not delete existing tables or fabricate migration-history rows to make startup pass. Validation
against a fresh, separately named test database does not repair or certify an older database.

Fresh broker topologies bind only versioned identifiers. Existing broker bindings are not removed
automatically by redeclaring topology; inspect any existing messages and remove obsolete bindings or
provision a fresh dedicated vhost before deploying.

## Open Questions

- [ ] None blocking.

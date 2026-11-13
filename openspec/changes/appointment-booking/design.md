# Design: Appointment Booking Service

## Technical Approach

Greenfield .NET 10 (`net10.0`; build with SDK 10 or later) service built as a **layered architecture with CQRS** across five projects, dependencies flowing in one direction:

```
API → ApplicationServices → Infrastructure → Domain → Contracts
```

- `Contracts` owns integration events plus public request/response DTOs (zero deps), including
  `BookingResult` and `WaitlistResult`.
- `Domain` owns entities, the slot state machine, and domain-facing repository/unit-of-work ports.
- `Infrastructure` owns EF Core + PostgreSQL repositories, the outbox store, and notification senders.
- `ApplicationServices` owns CQRS commands/handlers/validators, centralized API/business error codes,
  the event-dispatch port, background workers, and message dispatcher.
- `API` is a thin controllers layer + composition root.

The design hinges on three **swappable seams** that isolate every infrastructure decision:

| Seam | Today | Future (1 DI line to swap) |
|------|-------|-----------------------------|
| `IEventDispatcher` (`ApplicationServices/Messaging`) (transport) | `PollingDispatcher` | `RabbitMqDispatcher` (ADR-0001 evolution) |
| `INotificationSender` (`Infrastructure/Notifications`) (delivery) | Log + Webhook via `CompositeNotificationSender` | Sms/Email/Push/Twilio |
| `ISlotRepository` (persistence) | `SlotRepository` (EF Core + PostgreSQL) | Read-replica / multi-instance (hospital network) |

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
`HoldConfirmationRequested`; a polling worker dispatches it only while the held slot still matches the
event's patient and generation and has not expired. Expiry uses a polling worker plus lazy validity
checks. See ADR-0001 for the messaging evolution.

## Architecture Decisions

| Decision | Options | Tradeoff | Chosen |
|----------|---------|----------|--------|
| Persistence | PostgreSQL vs SQLite vs Redis/in-memory | SQLite is single-writer/embedded; Redis adds a service; in-memory loses state | **PostgreSQL** (Npgsql + docker compose) |
| Concurrency | Conditional UPDATE vs app mutex | Mutexes fail across processes | **Conditional transactional UPDATE + unique live-claim constraint** |
| Async messaging | Outbox vs real broker vs in-process channel | Broker breaks single-command startup; channel is volatile | **Transactional outbox + polling worker** (at-least-once) |
| Expiry | Polling worker vs in-process timer vs lazy only | Timers lost on restart; lazy-only stalls promotion | **Polling worker + lazy validity checks** |
| Offers | Exclusive expiring lease vs non-exclusive notify | Non-exclusive races to accept and breaks FIFO | **Exclusive offer lease with generation counter** |
| DB access | EF Core vs Dapper | EF migrations fluent; Dapper for raw conditional SQL | **EF Core** + `ExecuteUpdateAsync` for conditional transitions |
| Layering | Layered (linear deps) vs Hexagonal | Hexagonal keeps Infrastructure at the edge; layered is simpler to reason about | **Layered**: `API → ApplicationServices → Infrastructure → Domain → Contracts` |
| API style | Controllers + MediatR vs Minimal API | Minimal API scatters routing; controllers match team convention | **Controllers + MediatR (CQRS)** |
| Patient retention | Physical delete vs logical metadata + restrictive refs | Physical delete can orphan appointment and waitlist history | **Nullable `DeletedAtUtc`, no delete operation; restrictive FKs** |
| Patient NHC | App-only duplicate check vs PostgreSQL uniqueness | App-only checks race across requests and instances | **Optional trimmed NHC plus filtered unique index for non-null values** |
| Test types | Free-form strings vs persisted catalog | Free-form values drift and make persistence validation impossible | **Closed `TestTypes` catalog: BloodTest, Ultrasound, MRI** |

## Data Flow

Direct hold → asynchronous confirmation request:

```
API ── BlockSlot ──▶ ISlotRepository (conditional UPDATE AVAILABLE→HELD)
                         └── same unit of work: INSERT HoldConfirmationRequested outbox event
OutboxProcessorHostedService ──▶ PollingDispatcher (verify HELD + patient + generation + unexpired)
                                      └──▶ INotificationSender (please confirm before expiry)
```

Waitlist offer (complex flow):

```
ReleaseEvent ──▶ OfferNextWaiter handler (idempotent, per-slot generation)
   │  SELECT earliest waiter, conditional UPDATE slot AVAILABLE→OFFERED
   │  INSERT OfferCreated outbox event
   ▼
OutboxProcessor ──▶ OfferCreated ──▶ INotificationSender (log/webhook)
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

```csharp
// Recoletas.Booking.Domain (ports)
public interface ISlotRepository {
    Task<Slot?> GetByIdAsync(SlotId id, CancellationToken ct);
    Task<bool> TryHoldAsync(SlotId id, PatientId p, DateTimeOffset expires, CancellationToken ct); // conditional UPDATE
    Task<bool> TryConfirmAsync(SlotId id, PatientId p, int generation, CancellationToken ct);
    Task SaveAsync(Slot aggregate, CancellationToken ct);
}
public interface IOutboxStore {
    Task<IReadOnlyList<OutboxMessage>> LeaseAsync(int batch, CancellationToken ct);
    Task MarkProcessedAsync(Guid id, CancellationToken ct);
    Task MarkDeadAsync(Guid id, string reason, CancellationToken ct);
}
public interface IClock { DateTimeOffset UtcNow { get; } }
```

The transport seam `IEventDispatcher` is defined in `Recoletas.Booking.ApplicationServices.Messaging`;
the delivery seam `INotificationSender` is defined in `Recoletas.Booking.Infrastructure.Notifications`.
`HoldConfirmationRequested` is a primitive Contracts record containing `EventId`, `SlotId`, `PatientId`,
`TestType`, `Generation`, `ExpiresAtUtc`, and `OccurredAtUtc`. Successful outbox and notification logs
correlate by structured event ID/type; simulated notification logs include a safe delivery outcome, and
outbox failure logs include only the exception type and a retry/dead-letter disposition. Logs exclude
patient identifiers, NHC, recipient, subject, body, serialized payload, and exception objects or messages.

Slot state machine: `AVAILABLE → HELD → CONFIRMED → AVAILABLE` and `AVAILABLE → OFFERED → CONFIRMED/AVAILABLE`.
Domain enforces: no double-booking, hold expiry, offer window, generation counter. Integration events are
plain records in `Contracts/Events/` (no `IDomainEvent` marker).

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
application database. Repeatable database tests remove only rows associated with their deterministic
test-owned fixture identities.

## Migration / Rollout

The additive `PatientLifecycleAndTestTypeCatalog` migration adds `DeletedAtUtc`, creates and seeds
`TestTypes`, and adds restrictive FKs without removing existing rows. Before adding the type FKs, it
maps only the known legacy synthetic `BloodTest-{32 hex digits}` fixture values to `BloodTest`;
unknown persisted test types fail the migration with an explicit repair/mapping diagnostic. The
migration also stops before FK creation if existing patient/slot references are orphaned. No patient,
reservation, slot, or waitlist rows are deleted. Rollback of the feature remains an explicit schema
rollback; dropping the database is not required.

The additive `AddPatientNhcUniqueIndex` migration checks for duplicate NHC values after whitespace
normalization and aborts with a diagnostic containing the affected patient IDs before changing any
NHC values. If there are no collisions, it trims existing values and converts blanks to null before
creating `IX_Patients_Nhc` as a unique index filtered by `"Nhc" IS NOT NULL`. The migration never
deletes or merges patient records.

## Open Questions

- [ ] None blocking.

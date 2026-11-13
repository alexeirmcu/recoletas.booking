# Recoletas.Booking — Diagnostic Appointment Service

Backend service that manages diagnostic-test appointments (slots) for a healthcare network.
Slots are scarce, cancellations are frequent, and patients are always waiting. The service
guarantees **race-free exclusive booking**, **expiring holds**, and an **asynchronous
waitlist** that offers released slots to the next patient.

Built as a technical exercise. Uses **synthetic data only** — no real patient data.

## Quick start

Requires the .NET 10 SDK or later and Docker.

```bash
# 1. Start PostgreSQL
docker compose up -d

# 2. Run the API (applies migrations on startup; Development seeds demo slots)
dotnet run --project src/Recoletas.Booking.API

# 3. Open Swagger UI
# http://localhost:5000/swagger
```

The connection string is provided via the `ConnectionStrings__Booking` environment variable (set by
`Properties/launchSettings.json` for local development). Committed `appsettings*.json` only carry an
empty connection-string placeholder — no secrets are stored in the repo.

## Logging

The API uses Serilog.AspNetCore to route the existing `ILogger<T>` application and framework logs
through Serilog. Structured logs are written to stdout in every environment with an Information
minimum level; Microsoft framework messages are restricted to Warning and above. In Development only,
a daily rolling file is also written beneath the API content root at
`src/Recoletas.Booking.API/logs/recoletas-.log` (for example,
`src/Recoletas.Booking.API/logs/recoletas-20261113.log`). File output retains at most 14 files and
rolls when a file reaches 10 MiB. The generated `logs/` directory is ignored by Git.

Production deployments should use stdout with the platform's central log collection. If file logging
is explicitly enabled for a production deployment, mount a persistent volume for the log directory
and define its access and retention policy; this service does not enable production file logging.

Notification delivery logs contain structured `EventId`, `EventType`, and `DeliveryOutcome` fields.
Outbox logs correlate successful processing with `EventId` and `EventType`; failures additionally
record `FailureType` and a retry/dead-letter `Disposition`. These logs do not include patient IDs,
NHC, recipients, notification subjects or bodies, serialized payloads, exception objects, or exception
messages.

EF Core migrations are applied automatically at startup. Synthetic demo slots are seeded only when
the app runs in Development and `Booking:SeedDemoData` is enabled (`true` in
`appsettings.Development.json`; `false` by default in `appsettings.json`). The Infrastructure
initializer seeds only missing test-type/time-window combinations, so restarting the API does not
duplicate demo slots. Non-Development environments never seed demo data, even if the setting is
enabled.

Run tests:

```bash
dotnet test Recoletas.Booking.slnx
```

PostgreSQL integration and E2E tests use the isolated `recoletas_tests` database by default. They
derive the host, port, and credentials from `ConnectionStrings__Booking` (or use the local Docker
default) while replacing its database name; they never migrate or clean the application's
`recoletas` database. To use a different dedicated test database, set
`ConnectionStrings__BookingTests` before running tests, for example:

```bash
export ConnectionStrings__BookingTests='Host=localhost;Port=5433;Database=recoletas_tests;Username=postgres;Password=postgres'
dotnet test Recoletas.Booking.slnx
```

The test database is created if it does not exist and is not dropped by the test suite. Do not set
the test override to the application database.

## Documentation

| Document | Description |
|---|---|
| [Software Architecture Document (SAD)](doc/architecture/solution_arch.md) | Full architecture: solution structure, per-project detail, DI, HTTP contracts, concurrency model, tripwires. |
| [ADR-0001: Message broker adoption](doc/adr/0001-message-broker-adoption.md) | How the outbox evolves to RabbitMQ/Kafka. |
| [Project skills](skills/) | Coding conventions: `dotnet-clean-arch`, `cqrs-mediatr-patterns`, `api-rest-standards`, `dotnet-testing`. |
| [SDD artifacts](openspec/) | Spec-driven development: proposal, specs, design, tasks. |

## Architecture

Layered architecture with CQRS. Dependencies flow in one direction:

```
Recoletas.Booking.API ──▶ ApplicationServices ──▶ Infrastructure ──▶ Domain ──▶ Contracts
```

Stack: .NET 10, ASP.NET Core controllers + MediatR (CQRS) + FluentValidation, EF Core + PostgreSQL,
transactional outbox, MSTest + Moq. Full per-project detail lives in the
[SAD](doc/architecture/solution_arch.md).

Infrastructure owns the startup database initializer: it applies migrations and optionally seeds
synthetic slots using the injectable UTC clock. Seeding is Development-only and controlled by
`Booking:SeedDemoData`.

Public request and response DTOs, including `BookingResult` and `WaitlistResult`, live in the
dependency-free Contracts project. Stable API/business error values are centralized in
`ApplicationServices/Errors/BookingErrorCodes.cs` and documented in the OpenAPI definition.
The `GET /slots` agenda response contains the slot ID, test type, start/end instants, and availability
state; generation is not part of this read-only response. A successful hold returns its generation for
the subsequent confirmation request.

## Patient retention and test-type catalog

Patients are retained; `Patient.DeletedAtUtc` is nullable logical-deletion metadata only. There is
no patient deletion endpoint or physical-delete use case. PostgreSQL restricts deletion of patients
referenced by slots, reservations, or waitlist entries, and restricts deletion of slots referenced by
reservations. The closed `TestTypes` catalog contains exactly `BloodTest`, `Ultrasound`, and `MRI`;
slots and waitlist entries reference it through restrictive foreign keys. Application validation
rejects other new values, and the catalog is idempotently initialized in every environment.

Patient NHC is optional. Supplied values are trimmed, whitespace-only values become null, and
PostgreSQL's filtered unique index `IX_Patients_Nhc` prevents duplicate non-null NHC values, including
concurrent registrations. A duplicate returns HTTP 409 with `patient_nhc_already_exists`; multiple
patients may have no NHC. The migration reports existing duplicate values and stops without deleting
or merging patient records.

`Slots.CurrentReservationId` intentionally has no FK to `Reservations.Id`: the one-way
`Reservations.SlotId → Slots.Id` FK avoids a circular relationship. Its existing unique filtered
index is preserved.

## API

The HTTP contract, including all endpoints, schemas, security requirements, and error codes, is
documented in [the local OpenAPI definition](doc/openapi.yaml).

## (a) Decisions and discarded alternatives

- **Slot = test type + datetime window** (not a bare counter). A schedule of dated slots matches
  "show the agenda" and makes holds/offers realistic.
- **Waitlist per test type** (not a global FIFO): an MRI patient must never be offered a CT slot.
- **Concurrency enforced in the database** via a conditional transactional `UPDATE` + unique
  live-claim constraint (not in-memory locks, which are lost across instances and unsafe).
- **PostgreSQL + EF Core** (not SQLite): chosen for a realistic multi-service path; discarded the
  embedded DB to match production semantics. Trade-off: startup needs `docker compose up`.
- **Transactional outbox + polling worker** (not a broker-first design): the outbox solves the
  dual-write problem with no extra service; a real broker plugs in behind it later (see §c).
- **CQRS + MediatR + FluentValidation** (not a thin service layer): commands/handlers give a
  clean, testable use-case boundary and match team conventions.
- **Controllers** (not minimal API) + OpenAPI-first.

## (b) What was left out and why

- **Live authentication**: only a dev `X-Patient-Id` seam; OIDC (Keycloak/Azure AD) is documented
  but not wired — it adds credential/redirect friction without touching the graded logic.
- **Any UI**: Swagger is the interactive surface.
- **Real patient data**: synthetic only.
- **Payments/insurance, multi-center tenancy, production hardening**: out of scope for this slice;
  the seams for them are documented (§e).
- **A real message broker**: replaced by the outbox + worker; the broker path is an ADR.

## (c) Messaging delivery guarantees

Direct holds and waitlist notifications use a **transactional outbox**: a successful slot hold and its
`HoldConfirmationRequested` event, or a slot release and its resulting event, are committed in the
**same database transaction**. The HTTP request does not send notifications; a background worker
dispatches them asynchronously. Hold confirmation is sent only while the slot remains held by the same
patient at the same generation and the hold has not expired.

Delivery is **at-least-once** (not exactly-once):

| Failure | Behaviour |
|---|---|
| **Message lost** | The outbox row is durable; a crash cannot drop it. Retries with backoff. |
| **Message duplicated** | Dedup by message ID; handlers are idempotent; a duplicate is a no-op. |
| **Messages out of order** | Per-slot `generation` counter; stale events (older generation) are ignored. |

Outbox dispatch and simulated notification logs include structured event ID and event type fields for
correlation. They do not log patient IDs, NHC, recipients, notification subjects, or message bodies.

After a bounded retry count, an event moves to a **dead-letter** state for inspection/replay.

See [ADR-0001](doc/adr/0001-message-broker-adoption.md) for how this evolves to RabbitMQ/Kafka
behind the same outbox.

## (d) How real patient data would be handled

- Patients are identified by an **opaque server-generated GUID**, never by a national ID or NHC
  (número de historia clínica) as the join key. NHC is only an optional, unique patient attribute.
- In production: encrypt PII at rest, store the minimum necessary, pseudonymise in logs/metrics,
  enforce access control per center, and retain data per retention policy (right to erasure).
- The `Patient` aggregate + auth seam (`sub` → `patientId` via OIDC) is where these controls plug
  in without touching the booking logic.

## (e) Scaling to a whole hospital network

- Add a `centerId`/tenant column; scope slots, waitlists, and offers per center.
- PostgreSQL is already the store; for higher throughput, add connection pooling and move to a
  read-replica pattern for the agenda queries.
- Replace the outbox polling worker's `IEventDispatcher` with a **broker publisher** (RabbitMQ/Kafka)
  and run multiple consumer instances — the domain and handlers do not change (see the ADR).
- Introduce OIDC SSO for staff and a patient portal; add a per-center authorization layer.

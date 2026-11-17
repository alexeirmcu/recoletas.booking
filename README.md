# Recoletas.Booking — Diagnostic Appointment Service

Backend service that manages diagnostic-test appointments (slots) for a healthcare network.
Slots are scarce, cancellations are frequent, and patients are always waiting. The service
guarantees **race-free exclusive booking**, **expiring holds**, and an **asynchronous
waitlist** that offers released slots to the next patient.

Built as a technical exercise. Uses **synthetic data only** — no real patient data.

## Azure deployment

See [Azure CI/CD setup](doc/azure-deployment.md) for GitHub Actions, OIDC, container images,
infrastructure provisioning, controlled migrations, environment protection, and rollback.
RabbitMQ remains an externally provisioned durable AMQPS broker; it is not an ephemeral sidecar.
Azure requires `RabbitMq__UseTls=true` and port `5671` with a trusted server certificate.
Certificate validation is never bypassed.

`/health/live` checks HTTP process liveness; `/health/ready` checks PostgreSQL reachability and
the cached RabbitMQ connection, returning 503 when unavailable. These infrastructure probes are
anonymous and expose no dependency details. Deployments retain at least one active API replica
because workers and consumers run in the same process.

The container's `--migrate-only` argument applies migrations and seeds the test-type catalog, then
exits without starting HTTP or background workers. Set `Booking__ApplyMigrationsOnStartup=false`
when a deployment runs this migration job separately. Local startup migrations remain enabled by
default. Database upgrades must be compatible with the previous running image; application rollback
does not reverse migrations.

## Quick start

Requires the .NET 10 SDK or later and Docker.

```powershell
# 1. Start PostgreSQL and RabbitMQ
docker compose up -d

# 2. Set synthetic local-only broker credentials in this terminal
$env:RabbitMq__UserName = "booking_demo"
$env:RabbitMq__Password = "booking_demo"

# 3. Configure local JWT User Secrets as described in doc/jwt-auth.md, then run the API
#    (applies migrations on startup; Development seeds demo slots)
dotnet run --project src/Recoletas.Booking.API

# 4. Open Swagger UI
# http://localhost:5000/swagger
```

Swagger UI serves the checked-in [`doc/openapi.yaml`](doc/openapi.yaml) as its contract; update that
file when endpoint routes, request/response schemas, security, or error behavior changes.

The connection string is provided via the `ConnectionStrings__Booking` environment variable (set by
`Properties/launchSettings.json` for local development). Committed `appsettings*.json` contain empty
placeholders for local configuration; no secrets are stored in the repo.

RabbitMQ AMQP listens on `localhost:5672`; its management UI is at `http://localhost:15672`
(`booking_demo` / `booking_demo`, **local demo only**). Broker data uses the `rabbitmq_data` volume.
Set `RabbitMq__HostName`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__UserName`, and
`RabbitMq__Password` for other environments; use user-secrets or environment variables for credentials.
Missing broker credentials fail startup outside the Testing environment.

## Authentication and authorization

The API validates JWT bearer tokens; it does not issue tokens or implement a login flow. For this
demo, tokens use HS256 and the API validates the signature, issuer, audience, and expiration against
`Jwt:Secret`, `Jwt:Issuer`, and `Jwt:Audience`. The signing secret must be at least 32 bytes. The
algorithm is restricted to HS256.

Protected patient endpoints require a non-empty `sub`, exactly one `role: patient` claim, and one
`patientId` claim containing a GUID for the local patient. Admin tokens require `role: admin` and
must not contain `patientId`. `POST /patients` and infrastructure health probes are anonymous;
`GET /admin/appointments?from=...&to=...` is admin-only.

For local User Secrets setup and a step-by-step jwt.io patient-token example, see
[`doc/jwt-auth.md`](doc/jwt-auth.md). This is a demo validation mechanism, not a production
identity-provider integration.

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
Hold and offer notification logs also include `Generation`, needed to confirm the hold or accept the offer.
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

```powershell
$env:ConnectionStrings__BookingTests = "Host=localhost;Port=5433;Database=recoletas_tests;Username=postgres;Password=postgres"
dotnet test Recoletas.Booking.slnx
```

The test database is created if it does not exist and is not dropped by the test suite. Do not set
the test override to the application database. Schema and migration failures must fail tests rather
than being reported as unavailable PostgreSQL. Test hosts use isolated configuration, not
process-wide environment-variable changes.

Scenarios that require isolated FIFO, outbox, or pending-state populations use separate databases
named `bookingtest_<scenario>_<guid>` (including `waitlistfifo`, `waitliste2e`, `outboxlease`,
`pendingread`, `pendingflow`, and `holdconfirm`). They retain the
configured server and credentials but select their own database name, independently of the shared
test database override. The helper migrates and seeds each scenario database. These databases are
retained for inspection and are not automatically dropped; repeated runs create additional databases.

The baseline migration requires a fresh schema. If an existing database was created using older,
replaced migration IDs, diagnose its schema and migration history before planning an upgrade; do not
delete tables or insert migration-history rows to bypass a mismatch. A separately named fresh test
database can validate current migrations without modifying the existing database.

Focused messaging tests (no PostgreSQL required):

```bash
dotnet test tests/Recoletas.Booking.Tests --filter "TestCategory=Unit&FullyQualifiedName~Messaging"
```

The opt-in `RabbitMqTransportTests` uses a **dedicated empty test vhost**, not the application's `/`.
Create that vhost and grant a test user access, then set `RabbitMqTests__VirtualHost`,
`RabbitMqTests__UserName`, `RabbitMqTests__Password` (optionally `RabbitMqTests__HostName` and
`RabbitMqTests__Port`).
Run with `--filter FullyQualifiedName~RabbitMqTransportTests`. Without the vhost setting it is
explicitly inconclusive. The test declares the same topology with a short retry TTL; never point it
at a vhost containing application queues.

Booking-flow E2E tests use the Testing host and explicitly dispatch the persisted typed event when
exercising waitlist promotion. They validate HTTP/application/persistence behavior, not a live
RabbitMQ round trip; use the opt-in transport test for broker-level coverage.

## Documentation

| Document | Description |
|---|---|
| [Software Architecture Document (SAD)](doc/architecture/solution_arch.md) | Full architecture: solution structure, per-project detail, DI, HTTP contracts, concurrency model, tripwires. |
| [ADR-0001: Message broker adoption](doc/adr/0001-message-broker-adoption.md) | Accepted RabbitMQ topology, confirms, retries, and duplicate semantics. |
| [Project skills](skills/) | Coding conventions: `dotnet-clean-arch`, `cqrs-mediatr-patterns`, `api-rest-standards`, `dotnet-testing`. |
| [SDD artifacts](openspec/) | Spec-driven development: proposal, specs, design, tasks. |

## Architecture

Clean Architecture with CQRS. Dependencies point inward; the API is the composition root:

```
Recoletas.Booking.API ──▶ ApplicationServices ──▶ Domain
          │                         ▲
          └──────▶ Infrastructure ──┘
```

ApplicationServices owns use cases and ports; Infrastructure implements those ports and references
ApplicationServices, Domain, and Contracts. ApplicationServices references Domain and Contracts;
Domain and Contracts have no project dependencies. Domain owns its event records, which the
ApplicationServices boundary maps to integration events in Contracts. Stack: .NET 10,
ASP.NET Core controllers + MediatR (CQRS) + FluentValidation, EF Core + PostgreSQL, transactional
outbox + RabbitMQ, MSTest + Moq. Full per-project detail lives in the
[SAD](doc/architecture/solution_arch.md).

Infrastructure owns the startup database initializer: it applies migrations and optionally seeds
synthetic slots using the injectable UTC clock. Seeding is Development-only and controlled by
`Booking:SeedDemoData`.

RabbitMQ consumers are Infrastructure adapters hosted by the API. They deserialize deliveries and
delegate typed events to ApplicationServices, which owns workflow and notification decisions.
RabbitMQ client types, ACK/retry/DLQ handling, and concrete senders remain in Infrastructure.

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

`GET /appointments` returns confirmed appointments with their scheduled `startsAtUtc` and
`endsAtUtc`. `confirmedAtUtc` is separate metadata describing when the reservation was confirmed,
not when the diagnostic appointment occurs. The hold-confirm and offer-accept responses use the
same `AppointmentSummary` shape, including both scheduled times. All three timestamps are UTC.

### Pending confirmations for a patient website

`GET /appointments/pending` returns the authenticated patient's active holds and waitlist offers.
It uses the `patientId` from the patient JWT, not a caller-selected patient ID; admin tokens cannot
use this route. The response is an array, with `[]` when nothing is pending, and includes
`Cache-Control: no-store`. There is no pagination, history, or filtering in this demo endpoint.

Each item contains `slotId`, `testType`, `startsAtUtc`, `endsAtUtc`, `kind` (`hold` or `offer`),
`generation`, and `expiresAtUtc`. All dates are UTC. Results are ordered by the earliest expiry,
then slot start and slot ID. Only the patient's current Held/Offered slots whose lease expiry and
slot end are later than server time are returned, even if the expiry worker has not run yet.

Example response (synthetic data):

```json
[
  {
    "slotId": "11111111-1111-1111-1111-111111111111",
    "testType": "MRI",
    "startsAtUtc": "2030-01-01T12:00:00Z",
    "endsAtUtc": "2030-01-01T12:30:00Z",
    "kind": "offer",
    "generation": 3,
    "expiresAtUtc": "2030-01-01T11:55:00Z"
  }
]
```

Use each item's `kind` and returned `generation` with the existing action:

| kind | Action | JSON body |
|---|---|---|
| hold | `POST /holds/{slotId}/confirm` | `{"generation": <returned generation>}` |
| offer | `POST /offers/{slotId}/accept` | `{"generation": <returned generation>}` |

The read queries PostgreSQL directly; it does not require a notification to have been delivered,
extend leases, or change slot/waitlist/outbox state. It is a snapshot, not a booking guarantee:
the item may expire or change before a subsequent POST, which then returns the existing 409.
The future website should refresh pending state after an action or conflict. `GET /appointments`
continues to list only confirmed appointments. No website or new token flow is implemented here.

## (a) Decisions and discarded alternatives

- **Slot = test type + datetime window** (not a bare counter). A schedule of dated slots matches
  "show the agenda" and makes holds/offers realistic.
- **Waitlist per test type** (not a global FIFO): an MRI patient must never be offered a CT slot.
- **Concurrency enforced in the database** via conditional transactional `UPDATE`s guarded by state,
  expiry, and generation (not in-memory locks, which are lost across instances and unsafe).
- **PostgreSQL + EF Core** (not SQLite): chosen for a realistic multi-service path; discarded the
  embedded DB to match production semantics. Trade-off: startup needs `docker compose up`.
- **Transactional outbox + RabbitMQ** (not publishing in requests): the outbox solves the
  dual-write problem; its polling worker publishes with broker confirms (see §c).
- **CQRS + MediatR + FluentValidation** (not a thin service layer): commands/handlers give a
  clean, testable use-case boundary and match team conventions.
- **Controllers** (not minimal API) + OpenAPI-first.

## (b) What was left out and why

- **Provider-specific identity integration**: JWT bearer validation is wired, but token issuance,
  login, and mapping an external subject to a local patient remain the responsibility of the
  configured identity provider or trusted provisioning process.
- **Any UI**: Swagger is the interactive surface.
- **Real patient data**: synthetic only.
- **Payments/insurance, multi-center tenancy, production hardening**: out of scope for this slice;
  the seams for them are documented (§e).
- **Separate Worker executable and DB inbox**: publisher and consumers remain hosted services in the
  API for this demo; external notification effects can repeat.

## (c) Messaging delivery guarantees

Direct holds and waitlist notifications use a **transactional outbox**: a successful slot hold and its
`HoldConfirmationRequested` event, or a slot release and its resulting event, are committed in the
**same database transaction**. The HTTP request does not send notifications; a background worker
publishes them to RabbitMQ asynchronously. Rows are marked processed only after publisher confirmation;
mandatory unroutable messages, nacks, timeouts, and connection failures use existing outbox retries.
Consumers acknowledge only after handling. Hold confirmation is sent only while the slot remains held by the same
patient at the same generation and the hold has not expired.

Delivery is **at-least-once** (not exactly-once):

| Failure | Behaviour |
|---|---|
| **Publication failed** | Durable outbox retries with exponential backoff; marks processed only after broker confirmation. |
| **Message duplicated** | State/generation checks prevent duplicate active offers. Publishing before marking processed, retry forwarding before ack, or notifying before ack can all produce duplicates. Notification effects may repeat with the same MessageId. |
| **Messages out of order** | Current state/generation checks ignore stale slot events. No global or per-slot FIFO guarantee across retries. |

The release event's expected generation is enforced in the database offer transition. A waitlist
entry is claimed exclusively in the same transaction as the slot transition and OfferCreated
outbox event; competing consumers cannot consume the same entry for different slots. Failed offers
do not commit a claim. This is entry-level exclusivity, not a rule forbidding every simultaneous
appointment for the same patient.

Outbox completion and failure updates are fenced by lease acquisition ownership. A stale worker
cannot overwrite the state of a newer lease owner; this does not remove the possible duplicate
publication between broker confirmation and recording completion.

Event wire identifiers are explicit and versioned, independent of CLR class names. Only the versioned
identifiers are supported. The exact identifiers and deployment considerations are listed in
[ADR-0001](doc/adr/0001-message-broker-adoption.md#stable-event-identifiers-and-rollout).

The additive `20261114094732_OutboxLeaseFencing` migration adds nullable UUID `LeaseToken` without
replacing the baseline or rewriting old outbox rows. Current leases last five minutes and are not
renewed automatically; expired acquisitions must be recovered. Apply this migration before running
the updated publisher. It does not repair databases created from incompatible historical baselines.

Outbox dispatch and simulated notification logs include structured event ID and event type fields for
correlation. They do not log patient IDs, NHC, recipients, notification subjects, or message bodies.

The durable direct exchange `booking.events` routes `SlotReleased` to `booking.workflow` and
`HoldConfirmationRequested`, `OfferCreated`, and `AppointmentConfirmed` to `booking.notifications`.
All queues are durable quorum queues and messages are persistent with `EventId` as `MessageId`.
Each queue has a `.retry` queue (default 5-second TTL, at-least-once dead-lettering back to its source)
and a `.dlq`. Consumer failures get 5 attempts by default, then confirmed forwarding to the DLQ.
Forwarding must be confirmed before the original is acknowledged; failure closes/recreates the channel
and requeues unacknowledged deliveries. Broker outages retry connection every 5 seconds; they do not
silently discard messages. Repeated crashes/ack failures can exceed the application attempt budget.
Outbox publish failures still use their separate bounded retry/dead state in PostgreSQL.
Inspect DLQs in the management UI and fix the cause before manually replaying (retaining `MessageId`
and `Type`, resetting `booking-retry-count`). There is no automatic DLQ replay or DB inbox.
Changing retry TTL on existing queues requires a planned queue migration, not deleting live queues.
This single-node local broker is not an HA deployment. See [ADR-0001](doc/adr/0001-message-broker-adoption.md).

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
- Move the existing RabbitMQ hosted consumers/publisher into a separate Worker host and scale consumer
  instances; provision a multi-node quorum broker cluster (see the ADR).
- Introduce OIDC SSO for staff and a patient portal; add a per-center authorization layer.

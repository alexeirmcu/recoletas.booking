# Tasks: Appointment Booking Service

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 1,100–1,500 |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 (domain+core) → PR 2 (infrastructure+integration) → PR 3 (API+E2E+docs) |
| Delivery strategy | chained/stacked (resolved) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Notes |
|------|------|-----------|-------|
| 1 | Domain model, ports, and core use cases with unit tests | PR 1 | Base = `main` or feature tracker; no infrastructure |
| 2 | PostgreSQL repos, outbox, workers, notifiers, integration tests | PR 2 | Depends on PR 1; base = PR 1 branch if feature-branch-chain |
| 3 | Controllers API, auth middleware, E2E tests, README | PR 3 | Depends on PR 2; base = PR 2 branch if feature-branch-chain |

## Phase 1: Solution & Domain Foundation

- [x] 1.1 Create `Recoletas.Booking.slnx` and five `net10.0` projects for Contracts, Domain, ApplicationServices, Infrastructure, and API.
- [x] 1.2 Port `Slot`, `Patient`, `Reservation`, and `WaitlistEntry` plus `SlotId`, `PatientId`, `TestType`, and `SlotState` into the `Recoletas.Booking.Domain` namespace.
- [x] 1.3 Add the injectable UTC `IClock` service and preserve lease-expiry and generation rules.
- [x] 1.4 Define repository, outbox, dispatcher, notification, waitlist, and unit-of-work ports in the Domain project; remove `IDomainEvent`.
- [x] 1.5 Add primitive event and API records to the package-free Contracts project.

## Phase 2: Core Application Logic

- [x] 2.1 Preserve the slot state machine (hold, confirm, cancel, offer, accept, decline, and expiry) in the Domain entity.
- [x] 2.2 Implement the MediatR `BlockSlot` handler with conditional hold semantics and five-minute TTL.
- [x] 2.3 Implement confirmation and cancellation handlers with domain-raised Contracts events inside a unit of work.
- [x] 2.4 Implement `JoinWaitlist` enforcing one active entry per patient/test type.
- [x] 2.5 Implement offer, acceptance, decline, and expiry handlers with generation checks and five-minute default offer window.
- [x] 2.6 Implement `QueryAgenda` filtering by test type, date range, and future effective availability.
- [x] 2.7 Define plain primitive Contracts event records (`SlotReleased`, `OfferCreated`, `AppointmentConfirmed`).

## Phase 3: Infrastructure & Workers

- [x] 3.1 Add EF Core/Npgsql `BookingDbContext`, PostgreSQL model, initial migration, and model snapshot.
- [x] 3.2 Implement `SlotRepository` with conditional provider-translated updates for concurrent claims.
- [x] 3.3 Implement `OutboxStore` with JSON Contracts serialization, leasing, retries, and dead-letter handling.
- [x] 3.4 Implement `SystemClock`, `CompositeNotificationSender`, `LogNotificationSender`, and `WebhookNotificationSender`.
- [x] 3.5 Implement `PollingDispatcher` for stale-generation-safe event processing.
- [x] 3.6 Implement `ExpireHoldsHostedService` with polling and lazy expiry checks.
- [x] 3.7 Implement `OutboxProcessorHostedService` with retry/backoff and dead-letter handling.
- [x] 3.8 Reorganize EF Core context, configurations, converters, and migrations under `Infrastructure/EfModel`; require `ConnectionStrings__Booking` for the design-time factory.

## Phase 4: API & Composition

- [x] 4.1 Add controller endpoints for patients, agenda, holds, appointments, waitlist, and offer acceptance/decline.
- [x] 4.2 Add `X-Patient-Id` middleware and `IPatientIdentityResolver` implementation; reject missing or invalid identities.
- [x] 4.3 Register MediatR, validators, pipeline behaviors, and infrastructure composition.
- [x] 4.4 Add Swagger/OpenAPI and migrate plus seed the database at startup.

## Phase 5: Testing

- [x] 5.1 Recreate domain and application unit tests (including fake-clock lease, generation, and waitlist scenarios).
- [x] 5.2 Recreate PostgreSQL integration tests proving concurrent holds yield exactly one winner and outbox/state changes are atomic.
- [x] 5.3 Add E2E tests with `WebApplicationFactory` covering hold→confirm and waitlist→offer→accept flows.

## Phase 6: Documentation

- [x] 6.1 Add Docker Compose for PostgreSQL and write `README.md` with startup instructions, architecture, and decisions a–e referencing `doc/adr/0001-message-broker-adoption.md`.

## Phase 7: API Contracts & Error Codes

- [x] 7.1 Move `BookingResult` and `WaitlistResult` from ApplicationServices to Contracts; update consumers, OpenAPI schemas, README/SAD references, and tests.
- [x] 7.2 Centralize API/business error codes in `ApplicationServices/Errors/BookingErrorCodes.cs` and replace duplicated string literals across handlers, controllers, middleware, and tests.

## Phase 7 (continued): Messaging Port Ownership and MediatR Consistency

- [x] 7.3 Move `IEventDispatcher` to ApplicationServices and `INotificationSender` to Infrastructure; keep `IClock` in Domain.
- [x] 7.4 Convert `OfferNextWaiter` and `ExpireSlotLease` to MediatR requests and route dispatcher/expiry-worker calls through `IMediator`.
- [x] 7.5 Update handler tests and architecture documentation for the new port locations.

## Phase 8: Startup Initialization

- [x] 8.1 Extract EF migration and opt-in Development demo seeding from `Program.cs` into an idempotent Infrastructure initializer; configure seed enablement per environment and test repeated initialization.

## Phase 9: Persistence Integrity

- [x] 9.1 Add `Patient.DeletedAtUtc`; restrict Patient and Reservation→Slot references, keep `Slots.CurrentReservationId` without an FK, and add no physical patient-delete operation.
- [x] 9.2 Persist and idempotently initialize exactly `BloodTest`, `Ultrasound`, and `MRI`; restrict slot/waitlist catalog FKs, validate API inputs, migrate legacy rows safely, and add PostgreSQL integrity coverage.
- [x] 9.3 Document patient retention, FK policy, the closed TestType catalog, and the intentional absence of the CurrentReservationId FK in OpenSpec, OpenAPI, README, and the SAD.
- [x] 9.4 Isolate PostgreSQL tests from the development database and make the FIFO waitlist integration test repeatable across runs.
- [x] 9.5 Enforce unique optional NHC values with a PostgreSQL unique index; normalize blank values to null and report duplicate NHC as a conflict.
- [x] 9.6 Emit an outbox-backed hold-confirmation request after a successful hold and add privacy-safe structured logs for outbox dispatch and notification delivery.
- [x] 9.7 Configure Serilog console logging for all environments and a rolling file sink for Development; document location/retention and ensure structured notification/outbox logs exclude patient identifiers, recipients, payload contents, and exception details.
- [x] 9.8 Remove the unused `Generation` from the `GET /slots` agenda response (`SlotSummary`) and OpenAPI; retain generation for hold/offer confirmation flows.

# Proposal: Appointment Booking Service

## Intent

Build the backend managing diagnostic-test appointments for Recoletas Salud. Slots are scarce and cancellations frequent: the service must guarantee a race-free agenda, expiring holds, confirm/cancel, and an async waitlist offering released slots — one patient per slot.

## Scope

### In Scope
- REST API + Swagger UI: agenda query (FR-1), hold with TTL (FR-2), confirm/cancel (FR-3)
- Concurrency-safe exclusive assignment — hard requirement (FR-4, NFR-1)
- Per-test-type waitlist: async, exclusive, time-limited offers; FIFO advancement on timeout (FR-5, FR-6)
- Simulated notification (log + webhook) via transactional outbox (NFR-3)
- UTC storage, injectable clock, single-command startup, README decisions a–e (FR-7, NFR-2/4/5)

### Out of Scope
- Live auth (OIDC documented only), UI/frontend, real patient data, payments/insurance
- Multi-center network (README point e), production hardening, real broker

## Capabilities

### New Capabilities
- `slot-agenda`: slot model (test type + datetime window); query by test type and date range.
- `slot-booking`: hold/confirm/cancel lifecycle, TTL expiry, concurrency-safe exclusive assignment.
- `waitlist`: per-test-type join, exclusive async offers as leases with generation counter, acceptance window, FIFO advancement on timeout.
- `async-notification`: transactional outbox + polling worker; at-least-once delivery, state/generation-guarded transitions, retries with backoff, dead-letter; simulated log/webhook senders. Notification side effects may repeat after a crash before acknowledgement.

### Modified Capabilities
None — greenfield (`openspec/specs/` is empty).

## Approach

Per exploration (stack corrected to installed SDK): .NET 10 (`net10.0`) and PostgreSQL. The initial
implementation used five projects; the current architecture follows Clean Architecture: API composes
ApplicationServices and Infrastructure, ApplicationServices depends on Domain and Contracts, and
Infrastructure implements application ports.

- Slot = test type + datetime window; waitlist per test type; patient = server-generated GUID + minimal entity.
- Concurrency enforced in the database adapter (PostgreSQL): conditional transactional updates
  guarded by state, expiry, and generation; no in-memory locks.
- Expiry: polling worker + lazy validity checks; in-process timers rejected.
- Offers: exclusive leases with generation counter; conditional-update acceptance.
- Messaging: transactional outbox + background worker; real broker rejected (breaks single-command startup).
- Auth seam `IPatientIdentityResolver`: HS256 bearer JWT validation (`sub`, `patientId`, and role claims) configured with a demo signing secret, issuer, and audience; role-based patient/admin authorization.
- Defaults: hold TTL 5 min, offer window 5 min (configurable).

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Recoletas.Booking.Contracts` | New | Integration events + shared models (zero deps) |
| `src/Recoletas.Booking.Domain` | New | Entities, slot state machine, ports |
| `src/Recoletas.Booking.Infrastructure` | New | PostgreSQL repositories, outbox store, notifiers, clock |
| `src/Recoletas.Booking.ApplicationServices` | New | CQRS commands/handlers/validators, workers, dispatcher |
| `src/Recoletas.Booking.API` | New | Controllers, Swagger, auth middleware, DI |
| `README.md` | New | Startup, graded decisions a–e |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| PostgreSQL via Docker adds a startup step | Low | Single `docker compose up -d`; connection string overridable |
| Duplicate deliveries (at-least-once) | Med | State/generation checks protect slot transitions; notification side effects may repeat if delivery succeeds before a crash |
| Polling delay | Low | Lazy validity checks keep correctness independent of worker timing |

## Rollback Plan

Greenfield: delete `src/Recoletas.Booking.*`, `tests/`, `README.md`, and the Docker volume; nothing depends on this change.

## Dependencies

- .NET 10 target framework (SDK 10 or later) + Docker (PostgreSQL via `docker compose`); no broker or other external services.

## Success Criteria

- [ ] Single command starts service + Swagger UI
- [ ] Concurrent requests never double-assign a slot (proven by tests)
- [ ] Holds expire and offers time out correctly (injectable clock)
- [ ] Offers advance FIFO; notifications delivered at-least-once, visible in logs
- [ ] README documents decisions a–e

# AGENTS.md — Recoletas.Booking

Project conventions for AI agents working in this repository.

## What this project is

A .NET 10 backend service that manages diagnostic-test appointments (slots, expiring holds,
confirm/cancel, and an asynchronous waitlist with transactional-outbox notifications).
Synthetic data only.

## Architecture (Clean Architecture)

Dependencies point inward; the API is the composition root:

```
API → ApplicationServices → Domain
API → Infrastructure → ApplicationServices
Infrastructure → Domain
ApplicationServices → Contracts
Infrastructure → Contracts
```

| Project | Rules |
|---|---|
| `Recoletas.Booking.Contracts` | Zero package deps, zero project refs. Plain serializable records: integration events in `Events/`, shared models in `Models/`. |
| `Recoletas.Booking.Domain` | Entities, value objects, state machine, and domain-owned events. Zero project/package refs; no EF Core, MediatR, DI abstractions, or integration Contracts. |
| `Recoletas.Booking.ApplicationServices` | CQRS + MediatR + FluentValidation, use-case ports, workers, and event-to-contract mapping. References Domain and Contracts, never Infrastructure. |
| `Recoletas.Booking.Infrastructure` | EF Core + PostgreSQL (Npgsql); implements application ports. `Repositories/`, `Outbox/`, `Notifications/`, and concrete message dispatch adapters. References ApplicationServices and Domain. |
| `Recoletas.Booking.API` | Thin controllers (`_mediator.Send()`) and composition root in `Program.cs`; explicitly references ApplicationServices and Infrastructure. |

## Conventions

- **Commands** have no `Command`/`Query` suffix (`BlockSlot`, not `BlockSlotCommand`); handlers are
  `<Command>Handler`; validators are FluentValidation `AbstractValidator<T>`.
- **Controllers** are thin; responses use record types, never domain entities.
- **Concurrency**: slot exclusivity is enforced in the DB with conditional transactional updates
  guarded by state, expiry, and `generation`. Never use in-memory locks as the source of truth.
- **Outbox**: slot state change + outbox event are committed in the same transaction. Delivery is
  at-least-once; state transitions are guarded for idempotency, but external notification side effects
  may be repeated after a crash between sending and marking a message processed.
- **Boundaries**: repository, unit-of-work, clock, outbox, notification, and dispatcher ports live in
  ApplicationServices; Infrastructure implements them. Domain raises its own event records, which
  ApplicationServices maps to integration Contracts events.
- **Time**: UTC everywhere + injectable `IClock`.
- **Tests**: MSTest + Moq, AAA, directory mirroring, `Method_Scenario_Expected` naming.
- **Language**: all code, comments, and artifacts in English.

## Skills

Before implementing, read the matching skill in `skills/`:

- `dotnet-clean-arch` — layering and dependency rules.
- `cqrs-mediatr-patterns` — MediatR command/handler/validator conventions.
- `api-rest-standards` — REST endpoints and error codes.
- `dotnet-testing` — MSTest/Moq testing conventions.

## Key facts

- Solution file: `Recoletas.Booking.slnx` (target `net10.0`).
- Database: PostgreSQL via `docker compose up -d` (`Database=recoletas`).
- Run: `dotnet run --project src/Recoletas.Booking.API`.
- Hold TTL 5 min, waitlist offer window 5 min — both configurable via the `Booking` section of `appsettings.json` (`Booking__HoldTtl`, `Booking__OfferWindow` env vars).
- See `doc/adr/` for the message-broker evolution plan.

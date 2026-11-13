# AGENTS.md — Recoletas.Booking

Project conventions for AI agents working in this repository.

## What this project is

A .NET 10 backend service that manages diagnostic-test appointments (slots, expiring holds,
confirm/cancel, and an asynchronous waitlist with transactional-outbox notifications).
Synthetic data only.

## Architecture (Layered)

Dependencies flow in one direction:

```
API → ApplicationServices → Infrastructure → Domain → Contracts
```

| Project | Rules |
|---|---|
| `Recoletas.Booking.Contracts` | Zero package deps, zero project refs. Plain serializable records: integration events in `Events/`, shared models in `Models/`. |
| `Recoletas.Booking.Domain` | Entities + value objects + slot state machine + ports. Only `Microsoft.Extensions.DependencyInjection.Abstractions` allowed. No EF Core, no MediatR. |
| `Recoletas.Booking.Infrastructure` | EF Core + PostgreSQL (Npgsql). `Repositories/`, `Outbox/` (store), `Notifications/`. |
| `Recoletas.Booking.ApplicationServices` | CQRS + MediatR + FluentValidation + workers + dispatcher. `Commands/`, `Handlers/`, `Validators/`, `Workers/`, `Outbox/`, `Messaging/`. |
| `Recoletas.Booking.API` | Controllers only (`_mediator.Send()`). Composition root in `Program.cs`. |

## Conventions

- **Commands** have no `Command`/`Query` suffix (`BlockSlot`, not `BlockSlotCommand`); handlers are
  `<Command>Handler`; validators are FluentValidation `AbstractValidator<T>`.
- **Controllers** are thin; responses use record types, never domain entities.
- **Concurrency**: slot exclusivity is enforced in the DB with a conditional transactional
  `UPDATE` + unique live-claim constraint. Never use in-memory locks as the source of truth.
- **Outbox**: slot state change + outbox event are committed in the same transaction; delivery is
  at-least-once with idempotent handlers and per-slot `generation` ordering.
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

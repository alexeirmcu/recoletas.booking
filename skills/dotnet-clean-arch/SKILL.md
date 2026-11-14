---
name: dotnet-clean-arch
description: "Trigger: Layered architecture, layering, project structure, dependencies. Enforce strict layering and dependency rules for Recoletas.Booking."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract
Activate this skill when creating new projects, moving logic between layers, or reviewing architecture compliance.

## Hard Rules
- **Domain Purity**: `Recoletas.Booking.Domain` MUST have zero project and package references. No EF Core, MediatR, DI abstractions, or integration Contracts.
- **Contracts Purity**: `Recoletas.Booking.Contracts` MUST have zero package dependencies and zero project references — plain serializable records only.
- **Dependency Rule**: dependencies point inward:
  `API` → `ApplicationServices` → `Domain`; `API` → `Infrastructure` → `ApplicationServices` and `Domain`.
  ApplicationServices and Infrastructure may both depend on Contracts; Domain MUST NOT.
- **Infrastructure Isolation**: EF Core types (`DbContext`, `DbSet`) and PostgreSQL specifics MUST stay within `Recoletas.Booking.Infrastructure`.
- **Composition Root**: API references ApplicationServices and Infrastructure directly and wires them in `Program.cs`; controllers never touch Infrastructure types.
- **Ports and Adapters**: application-facing repository, unit-of-work, clock, outbox, notification, and dispatcher abstractions live in ApplicationServices. Infrastructure implements them.
- **Never Commit Secrets**: do not commit sensitive data (real connection strings, API keys, credentials). Provide them via environment variables or user-secrets; keep only empty placeholders in committed `appsettings*.json`. Local non-sensitive dev defaults (e.g. the docker `postgres`/`postgres`) may live in `launchSettings.json`.
- **No Business Logic in Controllers**: controllers must only build a command and call `_mediator.Send()`.
- **Event Boundary**: Domain owns domain-event records and MUST NOT reference integration Contracts. ApplicationServices maps domain events to plain integration-event records in `Recoletas.Booking.Contracts/Events/`. Do not add an `IDomainEvent` marker.

## Decision Gates
| Scenario | Action |
|----------|--------|
| New business rule | Implement in `Domain` (Entities/Value Objects) or `ApplicationServices` (Handlers). |
| New application use-case dependency | Define its port in `ApplicationServices/Abstractions/`, implement it in `Infrastructure`. |
| New API endpoint | Controller in `API` → Command in `ApplicationServices` → Handler in `ApplicationServices`. |
| New domain/integration event | Define domain-owned event data in Domain; map it in ApplicationServices to a plain record in `Contracts/Events/`. |

## Execution Steps
1. Identify the target layer.
2. Verify the dependency is allowed by the flow.
3. Implement using the correct project's naming conventions.
4. Ensure no leakage of infrastructure types into application/domain layers.

## Output Contract
- Code following Clean Architecture boundaries.
- Justification for any necessary exceptions in the Composition Root.

## References
- `openspec/changes/appointment-booking/design.md` — architecture breakdown.

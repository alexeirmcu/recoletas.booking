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
- **Domain Purity**: `Recoletas.Booking.Domain` MUST have zero framework dependencies (except `Microsoft.Extensions.DependencyInjection.Abstractions`). No EF Core, no MediatR.
- **Contracts Purity**: `Recoletas.Booking.Contracts` MUST have zero package dependencies and zero project references — plain serializable records only.
- **Dependency Flow**: dependencies flow in one direction:
  `API` → `ApplicationServices` → `Infrastructure` → `Domain` → `Contracts`.
- **Infrastructure Isolation**: EF Core types (`DbContext`, `DbSet`) and PostgreSQL specifics MUST stay within `Recoletas.Booking.Infrastructure`.
- **Composition Root**: Infrastructure is wired in `Program.cs` via `AddInfrastructure`; controllers never touch Infrastructure types.
- **Minimal References**: each project references only the next layer — `API → ApplicationServices → Infrastructure → Domain → Contracts` — relying on transitive project references for the rest.
- **Never Commit Secrets**: do not commit sensitive data (real connection strings, API keys, credentials). Provide them via environment variables or user-secrets; keep only empty placeholders in committed `appsettings*.json`. Local non-sensitive dev defaults (e.g. the docker `postgres`/`postgres`) may live in `launchSettings.json`.
- **No Business Logic in Controllers**: controllers must only build a command and call `_mediator.Send()`.
- **Integration Events live in Contracts**: cross-service events are plain records in `Recoletas.Booking.Contracts/Events/`. The Domain raises them; there is no `IDomainEvent` marker.

## Decision Gates
| Scenario | Action |
|----------|--------|
| New business rule | Implement in `Domain` (Entities/Value Objects) or `ApplicationServices` (Handlers). |
| New external API/DB call | Define interface in `Domain` (`Repository/` or `Services/`), implement in `Infrastructure`. |
| New API endpoint | Controller in `API` → Command in `ApplicationServices` → Handler in `ApplicationServices`. |
| New integration event | Add plain record to `Contracts/Events/`; raise it from the Domain. |

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

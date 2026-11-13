---
name: cqrs-mediatr-patterns
description: "Trigger: MediatR, Command, Handler, Query, CQRS. Enforce standard naming and implementation patterns for MediatR requests and handlers in Recoletas.Booking."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract
Activate this skill when implementing new use cases, refactoring handlers, or adding pipeline behaviors.

## Hard Rules
- **Command Naming**: commands MUST NOT use the `Command`/`Query` suffix (e.g. `BlockSlot`, not `BlockSlotCommand`).
- **Handler Naming**: handlers MUST follow `<Command>Handler` (e.g. `BlockSlotHandler`).
- **Location**: command/query records in `ApplicationServices/Commands/`; handlers in `ApplicationServices/Handlers/`; validators in `ApplicationServices/Validators/`.
- **Validation**: every command requiring business validation MUST have a FluentValidation validator.
- **Response Types**: return specific record types (Contracts models or result wrappers), never Domain entities.
- **Pipeline Behaviors**: register `LoggingBehavior` and `ValidationBehavior` in the correct order in `Program.cs`.

## Decision Gates
| Need | Action |
|------|--------|
| Read operation | Implement as a Query (record + handler) per project convention. |
| Cross-cutting concern | `IPipelineBehavior<TRequest,TResponse>`. |
| Complex validation | `IValidator<T>` in `ApplicationServices/Validators/`. |

## Execution Steps
1. Create the request record in `ApplicationServices/Commands/`.
2. Create the handler implementing `IRequestHandler<TRequest,TResponse>` in `Handlers/`.
3. Implement the validator with `AbstractValidator<T>` in `Validators/`.
4. Return a record result (Contracts model or result wrapper).

## Output Contract
- MediatR request + handler following the naming convention.
- Validated request pipeline.

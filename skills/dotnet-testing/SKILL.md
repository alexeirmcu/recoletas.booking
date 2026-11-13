---
name: dotnet-testing
description: "Trigger: MSTest, Moq, Handler tests, domain tests, unit tests. Testing conventions for Recoletas.Booking: domain tests mirror source structure, handler tests use Moq."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.1"
---

## Activation Contract
Activate this skill when writing new tests, adding edge-case tests, or fixing failing tests.

## Hard Rules

### General (all tests)
- **AAA Pattern**: strictly Arrange, Act, Assert — separate each section with a blank line.
- **Implicit Usings**: enabled.
- **Parallelism**: method-level (`ExecutionScope.MethodLevel` in `MSTestSettings.cs`).
- **Test Class**: `[TestClass] public sealed class {Name}Tests` — always `sealed`.
- **File-scoped namespaces**: `namespace Recoletas.Booking.Tests.{SourceNamespace};`.

### Domain Tests (pure unit tests, no mocking)
- **Directory mirroring**: test path mirrors the source path under `tests/Recoletas.Booking.Tests/`.
  - Source: `Recoletas.Booking.Domain/Entities/Slot.cs`
  - Test: `tests/Recoletas.Booking.Tests/Recoletas.Booking.Domain/Entities/SlotTests.cs`
- **One file per class**: `{ClassName}Tests.cs` — one `[TestClass]` per file.
- **Naming**: `Method_Scenario_Expected` (e.g. `Hold_WhenSlotFree_SetsHeld`).
- **No mocking**: domain tests are pure — no Moq, no dependencies.
- **Expected exceptions**: use manual try/catch when `Assert.ThrowsException<T>` is unavailable.
- **Helper factories**: `private static` factory methods for repeated Arrange (e.g. `CreateSlot()`).

### Handler Tests (orchestration with Moq)
- **Naming**: `Handle_<Scenario>_<Expected>` (e.g. `Handle_InvalidSlot_ReturnsConflict`).
- **Isolation**: use `Moq` to isolate the Handler from its dependencies.
- **Test Scope**: one `[TestClass]` per Handler.

## Decision Gates
| Test Type | Focus |
|-----------|-------|
| Domain Test | Pure logic: state machine, expiry, generation, equality. No mocking. |
| Handler Test | Orchestration: did it call the repo? did it map the response? |
| Integration Test | Real PostgreSQL (or test double) — concurrency, outbox atomicity. |
| Validation Test | FluentValidation rules. |

## Execution Steps
1. Mirror the source path under `tests/Recoletas.Booking.Tests/`.
2. Create `public sealed class {ClassName}Tests` with `[TestClass]`.
3. Use file-scoped namespace matching the path.
4. Add `private static` factory methods for repeated Arrange.
5. Write test methods following AAA with `Method_Scenario_Expected` naming.

## Output Contract
- Deterministic tests with clear naming and AAA structure.
- Domain tests in mirror directories, one file per class.
- High coverage of business-critical scenarios (concurrency, expiry, offer acceptance).

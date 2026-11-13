# Software Architecture Document (SAD) — Recoletas.Booking

## 1. Executive Summary & Design Goals

Recoletas.Booking is a backend service that manages diagnostic-test appointments: a limited set of
slots, expiring holds, confirm/cancel, and an asynchronous waitlist. The core design goal is
**correctness under concurrency** — a slot is never assigned to two patients — with a clean,
swappable architecture that can grow into a hospital network.

### Base technical specifications

| Aspect | Detail |
|---|---|
| Platform | .NET 10 SDK / C# |
| Pattern | Layered + CQRS |
| HTTP API | ASP.NET Core MVC Controllers (`[ApiController]`) |
| Mediator | MediatR with `LoggingBehavior<,>` + `ValidationBehavior<,>` |
| Validation | FluentValidation |
| Persistence | Entity Framework Core + PostgreSQL (Npgsql) |
| Messaging | Transactional outbox + background worker (at-least-once) |
| API Docs | Swashbuckle |
| Logging | Serilog.AspNetCore with Console and Development-only rolling File sinks |
| Tests | MSTest + Moq |

## 2. Solution Structure

```
/
├── Recoletas.Booking.slnx
├── src/
│   ├── Recoletas.Booking.Contracts/          # Integration events + shared models (zero deps)
│   ├── Recoletas.Booking.Domain/             # Entities, value objects, state machine, ports
│   ├── Recoletas.Booking.ApplicationServices/# Commands, Handlers, Validators, Workers, Outbox, Messaging
│   ├── Recoletas.Booking.Infrastructure/     # EF Core, PostgreSQL, repositories, outbox store, notifiers
│   └── Recoletas.Booking.API/                # Controllers, auth seam, composition root
├── tests/Recoletas.Booking.Tests/            # MSTest + Moq
├── skills/                                   # project skills
├── openspec/                                 # SDD artifacts
└── doc/adr/                                 # architecture decision records
```

### Project references

Dependencies flow in one direction (minimal references, the rest resolve transitively):

`API → ApplicationServices → Infrastructure → Domain → Contracts`

| Project | References |
|---|---|
| `API` | ApplicationServices |
| `ApplicationServices` | Infrastructure |
| `Infrastructure` | Domain |
| `Domain` | Contracts |
| `Contracts` | — (zero deps) |

Each project references only the next layer; the remaining types resolve via transitive project
references. The composition root (`Program.cs`) wires Infrastructure through `AddInfrastructure`
(available transitively); controllers never touch Infrastructure types.

## 3. Projects

### 3.1 `Recoletas.Booking.Contracts` (Class Library)

Zero package deps, zero project refs. The **public surface** a future service can reference without
pulling in the whole domain.

```
Contracts/
├── Events/
│   ├── HoldConfirmationRequested.cs # (Guid EventId, Guid SlotId, Guid PatientId, string TestType, int Generation, DateTimeOffset ExpiresAtUtc, DateTimeOffset OccurredAtUtc)
│   ├── SlotReleased.cs           # (Guid EventId, Guid SlotId, string TestType, int Generation, DateTimeOffset OccurredAtUtc)
│   ├── OfferCreated.cs           # (Guid EventId, Guid SlotId, Guid PatientId, string TestType, int Generation, DateTimeOffset ExpiresAtUtc, DateTimeOffset OccurredAtUtc)
│   └── AppointmentConfirmed.cs   # (Guid EventId, Guid SlotId, Guid PatientId, Guid ReservationId, string TestType, DateTimeOffset OccurredAtUtc)
└── Models/
    ├── SlotSummary.cs
    ├── AppointmentSummary.cs
    ├── PatientSummary.cs
    ├── BookingResult.cs           # hold/confirm/cancel/offer response
    ├── WaitlistResult.cs          # waitlist response
    ├── NotificationRequest.cs
    └── OutboxMessage.cs          # serialized envelope: (Guid Id, string EventType, string Payload, DateTimeOffset OccurredAtUtc)
```

`SlotSummary`, the `GET /slots` agenda response, contains the slot ID, test type, start/end instants,
and availability state. It intentionally omits generation: clients receive the generation from a
successful hold and submit it when confirming; offer acceptance likewise uses its offer generation.

Events are plain records with **primitive fields only** — no `IDomainEvent` marker, no domain types —
so they serialize cleanly and a second service can consume them without referencing `Domain`.

### 3.2 `Recoletas.Booking.Domain` (Class Library)

Pure domain. Zero framework deps (only `Microsoft.Extensions.DependencyInjection.Abstractions`).

```
Domain/
├── Entities/
│   ├── Slot.cs                   # state machine + holds/offers
│   ├── Reservation.cs
│   ├── Patient.cs                # id + name + optional nhc + createdAtUtc + nullable DeletedAtUtc
│   └── WaitlistEntry.cs
├── ValueObjects/
│   ├── SlotId.cs
│   ├── PatientId.cs
│   ├── TestType.cs
│   └── SlotState.cs              # Available, Held, Offered, Confirmed
├── Repository/                   # persistence ports (interfaces)
│   ├── ISlotRepository.cs
│   ├── IAppointmentRepository.cs
│   ├── IPatientRepository.cs
│   ├── IWaitlistRepository.cs
│   ├── IOutboxStore.cs
│   ├── IUnitOfWork.cs
│   └── SlotWrapper.cs            # SlotWrapper<T>(Slot, T) — mutation result
└── Services/
    └── IClock.cs                  # time abstraction
```

#### Slot state machine

```
Available ──hold──▶ Held ──confirm──▶ Confirmed ──cancel──▶ Available
    ▲                 │ (expires)                          ▲
    │                 ▼                                   │
    └───────────── Available ◀────────────────────────────┘
                   │ (waitlist offer)
                   ▼
                 Offered ──accept──▶ Confirmed
                   │ (window expires / decline)
                   └──────────────▶ next waiter or Available
```

Key invariants (enforced in the domain, guaranteed in the DB):
- **Hold TTL** (default 5 min) — an expired hold is never valid, even if the worker lags (lazy check).
- **Offer window** (default 5 min) — an offer is an exclusive lease with a `generation` counter.
- **Generation** — every offer/claim increments `generation`; stale events are ignored.

### 3.3 `Recoletas.Booking.ApplicationServices` (Class Library)

Orchestration: use cases, workers, and message dispatch. References Infrastructure, Domain, and
Contracts. Uses no EF Core types directly.

**NuGet:** `MediatR`, `FluentValidation`, `FluentValidation.DependencyInjectionExtensions`,
`Microsoft.Extensions.Hosting.Abstractions`.

```
ApplicationServices/
├── Commands/
│   ├── RegisterPatient.cs        # : IRequest<PatientSummary>
│   ├── BlockSlot.cs              # : IRequest<BookingResult>
│   ├── ConfirmHold.cs
│   ├── CancelAppointment.cs
│   ├── JoinWaitlist.cs
│   ├── AcceptOffer.cs
│   ├── DeclineOffer.cs
│   ├── QueryAgenda.cs            # query: testType, from, to → IReadOnlyList<SlotSummary>
│   └── QueryAppointments.cs
├── Handlers/
│   ├── RegisterPatientHandler.cs
│   ├── BlockSlotHandler.cs
│   ├── ConfirmHoldHandler.cs
│   ├── CancelAppointmentHandler.cs
│   ├── JoinWaitlistHandler.cs
│   ├── AcceptOfferHandler.cs
│   ├── DeclineOfferHandler.cs
│   ├── QueryAgendaHandler.cs
│   ├── QueryAppointmentsHandler.cs
│   ├── ExpireSlotLeaseHandler.cs # worker-driven expiry
│   ├── OfferNextWaiterHandler.cs # worker-driven promotion
│   └── OutboxEvents.cs           # outbox → domain event mapping
├── Validators/
│   └── BookingValidators.cs      # FluentValidation AbstractValidator<T> per command
├── Behaviors/
│   ├── LoggingBehavior.cs
│   └── ValidationBehavior.cs
├── Errors/
│   └── BookingErrorCodes.cs      # stable API/business error code constants
├── Outbox/
│   ├── OutboxProcessor.cs        # retry + dead-letter dispatch
│   └── OutboxProcessorOptions.cs
├── Messaging/
│   ├── IEventDispatcher.cs       # transport seam (outbox → handling)
│   └── PollingDispatcher.cs      # deserializes events, sends MediatR commands
├── Workers/
│   ├── ExpireHoldsHostedService.cs
│   ├── ExpireHoldsOptions.cs
│   └── OutboxProcessorHostedService.cs
└── ApplicationServiceRegistration.cs  # AddApplicationServices(...)
```

Conventions: commands have **no `Command`/`Query` suffix**; handlers are `<Command>Handler`;
handlers return record results, never domain entities.

### 3.4 `Recoletas.Booking.Infrastructure` (Class Library)

Concrete implementations. EF Core + PostgreSQL types stay here.

**NuGet:** `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Tools`,
`Npgsql.EntityFrameworkCore.PostgreSQL`.

```
Infrastructure/
├── EfModel/
│   ├── BookingDbContext.cs
│   ├── BookingDbContextFactory.cs
│   ├── BookingDatabaseInitializer.cs # startup migrations + idempotent Development demo seed
│   ├── TestTypeCatalogEntry.cs    # persisted closed test-type catalog row
│   ├── OutboxRecord.cs
│   ├── UnitOfWork.cs              # ambient transaction
│   ├── IConstraintViolationDetector.cs
│   ├── PostgresConstraintViolationDetector.cs
│   ├── EntityConfiguration/
│   │   ├── ValueConverters.cs
│   │   ├── SlotConfiguration.cs
│   │   ├── PatientConfiguration.cs
│   │   ├── ReservationConfiguration.cs
│   │   ├── WaitlistEntryConfiguration.cs
│   │   ├── TestTypeCatalogConfiguration.cs
│   │   └── OutboxRecordConfiguration.cs
│   └── Migrations/
│       ├── 20261112211013_InitialBooking.cs
│       ├── 20261113103210_PatientLifecycleAndTestTypeCatalog.cs
│       ├── 20261113121711_AddPatientNhcUniqueIndex.cs
│       └── BookingDbContextModelSnapshot.cs
├── Repositories/
│   ├── SlotRepository.cs         # conditional claim via ExecuteUpdateAsync
│   ├── AppointmentRepository.cs
│   ├── PatientRepository.cs
│   └── WaitlistRepository.cs
├── Outbox/
│   └── OutboxStore.cs            # implements IOutboxStore; serializes Contracts events
├── Notifications/
│   ├── INotificationSender.cs    # delivery seam
│   ├── LogNotificationSender.cs
│   ├── WebhookNotificationSender.cs
│   └── CompositeNotificationSender.cs
├── Clock/SystemClock.cs
└── InfrastructureServiceRegistration.cs   # AddInfrastructure(connectionString)
```

### 3.5 `Recoletas.Booking.API` (ASP.NET Core Web API)

**NuGet:** `Swashbuckle.AspNetCore`, `MediatR`, `FluentValidation.DependencyInjectionExtensions`.

```
API/
├── Program.cs                     # composition root
├── Controllers/
│   ├── PatientsController.cs
│   ├── SlotsController.cs
│   ├── AppointmentsController.cs
│   └── WaitlistController.cs
├── Auth/
│   ├── IPatientIdentityResolver.cs
│   └── PatientIdentityResolver.cs  # dev: reads the patient id from HttpContext.Items
└── Middleware/
    ├── PatientIdentityMiddleware.cs  # extracts/validates X-Patient-Id (401 if missing/invalid)
    └── ExceptionHandlingMiddleware.cs
```

Controller rule: `[HttpVerb] → build command → await _mediator.Send(command) → return Ok(...)`.

## 4. Startup & Dependency Registration

```csharp
// Provided via the ConnectionStrings__Booking env var (launchSettings.json); fail-fast if empty.
var connectionString = builder.Configuration.GetConnectionString("Booking")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Booking' is not configured.");

builder.Services.AddControllers();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<IPatientIdentityResolver, PatientIdentityResolver>();
var bookingOptions = builder.Configuration.GetSection("Booking").Get<ApplicationServicesOptions>()
    ?? new ApplicationServicesOptions();
builder.Services.AddApplicationServices(bookingOptions);  // MediatR, validators, handlers, workers, dispatcher
builder.Services.AddInfrastructure(connectionString);    // DbContext, repositories, outbox store, notifiers

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<PatientIdentityMiddleware>();
app.MapControllers();
// after Build: resolve BookingDatabaseInitializer in a scope and initialize the database
// seedDemoData = app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Booking:SeedDemoData")
```

`BookingDatabaseInitializer` always applies EF Core migrations. It seeds the three synthetic demo
slots only when the API passes an enabled seed flag; `Program.cs` derives that flag from both the
Development environment and `Booking:SeedDemoData`. The initializer uses `IClock` for tomorrow's UTC
windows and checks exact test type/start/end values before inserting each slot. The base setting is
`false`; `appsettings.Development.json` opts in with `true`, while other environments remain
unseeded regardless of configuration.

The initializer also idempotently seeds the `TestTypes` catalog (`BloodTest`, `Ultrasound`, and `MRI`)
in every environment after migrations and before optional demo-slot seeding.

### Logging

The API composition root integrates Serilog.AspNetCore with the Microsoft.Extensions.Logging pipeline,
so existing `ILogger<T>` calls are preserved and routed to Serilog. The minimum level is Information;
Microsoft framework categories are overridden to Warning. Console output is enabled for every
environment. Development additionally writes beneath the API content root to
`logs/recoletas-.log`, rolling daily, retaining at most 14 files, and rolling when a file reaches
10 MiB. Runtime logs are excluded by the root `logs/` ignore rule. Production file logging is disabled;
production deployments should use stdout and central collection, or explicitly mount a persistent
volume if file logging is enabled operationally.

Simulated notification logs contain structured `EventId`, `EventType`, and `DeliveryOutcome` fields.
Outbox success logs contain `EventId` and `EventType`; failure logs additionally contain `FailureType`
and a retry/dead-letter `Disposition`. These logs exclude patient IDs, NHC, recipient, subject, body,
serialized payload, and exception objects or messages.

The API references Serilog.AspNetCore 10.0.0, Serilog.Sinks.Console 6.1.1, and
Serilog.Sinks.File 7.0.0.

**Lifetimes:** `Scoped` — handlers, repositories, DbContext; `Singleton` — hosted workers,
`SystemClock`; `Transient` — FluentValidation validators.

## 5. HTTP Contracts

The local [OpenAPI definition](../openapi.yaml) is the single source of truth for HTTP endpoints,
request and response schemas, security requirements, and error codes. Public response records live in
Contracts; their `ErrorCode` values are centralized in `ApplicationServices/Errors/BookingErrorCodes.cs`.

## 6. Concurrency Model

Slot exclusivity is enforced **in the database**, never in memory:

```sql
UPDATE slots
SET state = 'Held', patient_id = @patientId, expires_at = @expiresAt, generation = generation + 1
WHERE id = @slotId AND state = 'Available'
  AND (expires_at IS NULL OR expires_at <= @now);
-- rows affected == 0 → someone else claimed it
```

- A **unique live-claim constraint** is the second invariant: two concurrent claims cannot both commit.
- The same conditional-update pattern guards offer acceptance (`generation` + `expires_at` match).
- `PostgresConstraintViolationDetector` maps unique-violation errors to a `409 Conflict` result.

### Persistence integrity and patient retention

`Patient.DeletedAtUtc` records optional logical-deletion metadata; this service has no physical patient
deletion endpoint or operation. `Slots.AssignedPatientId → Patients.Id`,
`Reservations.PatientId → Patients.Id`, `WaitlistEntries.PatientId → Patients.Id`,
`Reservations.SlotId → Slots.Id`, and both test-type references to `TestTypes.Code` use restrictive
delete behavior. `Slots.CurrentReservationId` retains its unique filtered index but deliberately has
no FK to `Reservations.Id`; the one-way reservation-to-slot FK avoids a circular relationship.

Patient NHC is optional and normalized by trimming surrounding whitespace; blank values become null.
The filtered unique index `IX_Patients_Nhc` enforces uniqueness for non-null values, including races.
Only that named PostgreSQL constraint violation becomes HTTP 409 `patient_nhc_already_exists`.
The additive migration diagnoses pre-existing duplicate values after normalization and stops without
deleting or merging patient rows.

The catalog is closed to the exact codes `BloodTest`, `Ultrasound`, and `MRI`. QueryAgenda and
JoinWaitlist validate new values at the ApplicationServices boundary, while the database FKs reject
unregistered values written outside the API. The additive migration preserves rows: it maps only the
known historical synthetic `BloodTest-{32 hex digits}` test fixture code to `BloodTest`, and fails
with an explicit diagnostic before adding FKs when unknown catalog values or orphan references are
found. It never deletes patient, reservation, waitlist, or slot data.

## 7. Messaging (Transactional Outbox)

Successful direct holds and slot releases write their domain state change **and** corresponding outbox
event in the **same transaction**. `OutboxProcessorHostedService` polls and dispatches. A
`HoldConfirmationRequested` event produces a simulated “please confirm before expiry” notification only
when the current slot remains Held, assigned to the same patient, at the same generation, and unexpired.

- **At-least-once** delivery; dedup by message ID; idempotent handlers.
- **Per-slot `generation`** ordering — stale events are ignored.
- Retry with backoff → **dead-letter** after a bounded count.
- Structured outbox and notification delivery logs include event ID and type, never patient ID, NHC,
  recipient, subject, or notification body.
- Notification delivery logs may additionally include only the safe structured outcome (`Succeeded`).

The dispatcher port (`IEventDispatcher`) is the swappable seam: `PollingDispatcher` today,
`RabbitMqDispatcher` later — see `doc/adr/0001-message-broker-adoption.md`.

## 8. Tripwires

- No business logic in controllers — only `_mediator.Send()`.
- No EF types (`DbContext`, `DbSet`, `IQueryable<T>`) outside Infrastructure.
- No in-memory locks as the source of truth for slot exclusivity.
- No `IDomainEvent` marker — integration events are plain Contracts records.
- UTC everywhere + injectable `IClock`.
- No `Thread.Sleep`, `.Result`, `.Wait()`, or `async void`.
- All code, comments, and artifacts in English.

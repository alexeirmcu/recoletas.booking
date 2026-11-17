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
| Pattern | Clean Architecture + CQRS |
| HTTP API | ASP.NET Core MVC Controllers (`[ApiController]`) |
| Mediator | MediatR with `LoggingBehavior<,>` + `ValidationBehavior<,>` |
| Validation | FluentValidation |
| Persistence | Entity Framework Core + PostgreSQL (Npgsql) |
| Messaging | Transactional outbox + RabbitMQ confirmed publisher and hosted consumers (at-least-once) |
| API Docs | Swashbuckle |
| Logging | Serilog.AspNetCore with Console and Development-only rolling File sinks |
| Tests | MSTest + Moq |

## 2. Solution Structure

```
/
├── Recoletas.Booking.slnx
├── src/
│   ├── Recoletas.Booking.Contracts/          # Integration events + shared models (zero deps)
│   ├── Recoletas.Booking.Domain/             # Entities, value objects, state machine, domain events
│   ├── Recoletas.Booking.ApplicationServices/# Use cases, ports, handlers, workers, event mapping
│   ├── Recoletas.Booking.Infrastructure/     # EF Core, PostgreSQL, port adapters, outbox and dispatch
│   └── Recoletas.Booking.API/                # Controllers, auth seam, composition root
├── tests/Recoletas.Booking.Tests/            # MSTest + Moq
├── skills/                                   # project skills
├── openspec/                                 # SDD artifacts
└── doc/adr/                                 # architecture decision records
```

### Project references

Dependencies point inward. The API is the composition root and directly references both
ApplicationServices and Infrastructure:

| Project | References |
|---|---|
| `API` | ApplicationServices, Infrastructure |
| `ApplicationServices` | Domain, Contracts |
| `Infrastructure` | ApplicationServices, Domain, Contracts |
| `Domain` | — (zero project/package dependencies) |
| `Contracts` | — (zero deps) |

Infrastructure implements application-owned ports. Domain has no reference to EF Core, MediatR,
dependency-injection abstractions, or integration Contracts. Controllers never touch Infrastructure
types; only `Program.cs` wires both outer layers.

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
    ├── PendingAppointmentSummary.cs # current patient hold/offer + generation and expiry
    ├── PatientSummary.cs
    ├── AdminAppointmentSummary.cs
    ├── BookingResult.cs           # hold/confirm/cancel/offer response
    ├── WaitlistResult.cs          # waitlist response
    ├── NotificationRequest.cs
    └── OutboxMessage.cs          # serialized envelope plus optional acquisition LeaseToken
```

`SlotSummary`, the `GET /slots` agenda response, contains the slot ID, test type, start/end instants,
and availability state. It intentionally omits generation: clients receive the generation from a
successful hold and submit it when confirming; offer acceptance likewise uses its offer generation.

Events are plain records with **primitive fields only** — no `IDomainEvent` marker, no domain types —
so they serialize cleanly and a second service can consume them without referencing `Domain`.

### 3.2 `Recoletas.Booking.Domain` (Class Library)

Pure domain with zero package and project references. It owns entities, value objects, state
transitions, and domain-event records; it does not depend on persistence, application ports, or
integration Contracts.

```
Domain/
├── Entities/
│   ├── Slot.cs                   # state machine + holds/offers
│   ├── Reservation.cs
│   ├── Patient.cs                # id + name + optional nhc + createdAtUtc + nullable DeletedAtUtc
│   └── WaitlistEntry.cs
├── Events/
│   └── SlotDomainEvents.cs       # domain-owned event records (no marker interface)
├── ValueObjects/
│   ├── SlotId.cs
│   ├── PatientId.cs
│   ├── TestType.cs
│   └── SlotState.cs              # Available, Held, Offered, Confirmed
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

Orchestration: use cases, application ports, workers, and domain-to-integration event mapping.
References Domain and Contracts, never Infrastructure. Uses no EF Core types directly.

**NuGet:** `MediatR`, `FluentValidation`, `FluentValidation.DependencyInjectionExtensions`,
`Microsoft.Extensions.Hosting.Abstractions`.

```
ApplicationServices/
├── Abstractions/                 # use-case ports implemented by Infrastructure
│   ├── ISlotRepository.cs
│   ├── IAppointmentRepository.cs
│   ├── IAdminAppointmentReadRepository.cs
│   ├── IPatientRepository.cs
│   ├── IWaitlistRepository.cs
│   ├── IOutboxStore.cs
│   ├── IUnitOfWork.cs
│   ├── IClock.cs
│   └── SlotWrapper.cs
├── Commands/
│   ├── RegisterPatient.cs        # : IRequest<PatientSummary>
│   ├── BlockSlot.cs              # : IRequest<BookingResult>
│   ├── ConfirmHold.cs
│   ├── CancelAppointment.cs
│   ├── JoinWaitlist.cs
│   ├── AcceptOffer.cs
│   ├── DeclineOffer.cs
│   ├── QueryAgenda.cs            # query: testType, from, to → IReadOnlyList<SlotSummary>
│   ├── QueryAppointments.cs
│   ├── QueryPendingAppointments.cs # own active holds/offers, no pagination
│   └── QueryAdminAppointments.cs # confirmed admin view, bounded by from/to
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
│   ├── QueryPendingAppointmentsHandler.cs
│   ├── QueryAdminAppointmentsHandler.cs
│   ├── ExpireSlotLeaseHandler.cs # worker-driven expiry
│   ├── OfferNextWaiterHandler.cs # worker-driven promotion
│   ├── SlotReleasedEventHandler.cs # typed release event -> generation-guarded promotion
│   └── OutboxEvents.cs           # outbox → domain event mapping
├── Validators/
│   └── BookingValidators.cs      # FluentValidation AbstractValidator<T> per command
├── Behaviors/
│   ├── LoggingBehavior.cs
│   └── ValidationBehavior.cs
├── Errors/
│   ├── BookingErrorCodes.cs      # stable API/business error code constants
│   └── DuplicatePatientNhcException.cs
├── Outbox/
│   ├── OutboxProcessor.cs        # retry + dead-letter dispatch
│   └── OutboxProcessorOptions.cs
├── Messaging/
│   ├── IEventDispatcher.cs       # dispatch port
│   ├── INotificationSender.cs    # notification-delivery port
│   └── NotificationEventHandler.cs # typed events, state/expiry checks, notification requests
├── Workers/
│   ├── ExpireHoldsHostedService.cs
│   ├── ExpireHoldsOptions.cs
│   └── OutboxProcessorHostedService.cs
└── ApplicationServiceRegistration.cs  # AddApplicationServices(...)
```

Conventions: commands have **no `Command`/`Query` suffix**; handlers are `<Command>Handler`;
handlers return record results, never domain entities.

### 3.4 `Recoletas.Booking.Infrastructure` (Class Library)

Concrete adapters. EF Core + PostgreSQL types stay here. Infrastructure references ApplicationServices
to implement its ports, Domain for entities, and Contracts for integration event serialization.
ApplicationServices never references Infrastructure.

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
│       ├── 20261113205009_InitialBooking.cs
│       ├── 20261114094732_OutboxLeaseFencing.cs # additive nullable UUID LeaseToken
│       └── BookingDbContextModelSnapshot.cs
├── Repositories/
│   ├── SlotRepository.cs         # conditional claim via ExecuteUpdateAsync
│   ├── AppointmentRepository.cs
│   ├── AdminAppointmentReadRepository.cs
│   ├── PatientRepository.cs
│   └── WaitlistRepository.cs
├── Outbox/
│   └── OutboxStore.cs            # implements the application IOutboxStore port
├── Notifications/
│   ├── LogNotificationSender.cs
│   ├── WebhookNotificationSender.cs
│   └── CompositeNotificationSender.cs
├── Messaging/
│   ├── RabbitMqDispatcher.cs     # IEventDispatcher: persistent mandatory publish + confirms
│   ├── RabbitMqConsumersHostedService.cs # workflow and notification queue consumers
│   ├── RabbitMqDeliveryHandler.cs # handle, confirmed retry/DLQ forwarding, then ack
│   ├── RabbitMqConnection.cs     # shared connection, isolated confirm-enabled channels
│   ├── RabbitMqTopology.cs       # durable direct exchange and quorum queues
│   ├── RabbitMqOptions.cs
│   └── BookingEventSerializer.cs # stable versioned wire identifiers
├── Clock/SystemClock.cs
└── InfrastructureServiceRegistration.cs   # AddInfrastructure(connectionString)
```

### 3.5 `Recoletas.Booking.API` (ASP.NET Core Web API)

**NuGet:** `Microsoft.AspNetCore.Authentication.JwtBearer`, `Swashbuckle.AspNetCore`, `MediatR`,
`FluentValidation.DependencyInjectionExtensions`.
The API references ApplicationServices and Infrastructure directly; it is the composition root.

```
API/
├── Program.cs                     # composition root
├── Controllers/
│   ├── PatientsController.cs
│   ├── SlotsController.cs
│   ├── AppointmentsController.cs
│   ├── AdminAppointmentsController.cs
│   └── WaitlistController.cs
├── Auth/
│   ├── IPatientIdentityResolver.cs
│   ├── PatientIdentityResolver.cs  # reads and validates the patientId JWT claim
│   └── BookingAuthenticationExtensions.cs # JWT validation and role policies
└── Middleware/
    └── ExceptionHandlingMiddleware.cs
```

Controller rule: `[HttpVerb] → build command → await _mediator.Send(command) → return Ok(...)`.

## 4. Startup & Dependency Registration

```csharp
// Provided via the ConnectionStrings__Booking env var (launchSettings.json); fail-fast if empty.
var connectionString = builder.Configuration.GetConnectionString("Booking")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Booking' is not configured.");

builder.Services.AddControllers();
builder.Services.AddScoped<IPatientIdentityResolver, PatientIdentityResolver>();
builder.Services.AddBookingAuthentication(builder.Configuration);
var bookingOptions = builder.Configuration.GetSection("Booking").Get<ApplicationServicesOptions>()
    ?? new ApplicationServicesOptions();
builder.Services.AddApplicationServices(bookingOptions);  // use cases, validators, workers, ports
builder.Services.AddInfrastructure(connectionString);    // adapters, DbContext, repositories, outbox, senders
builder.Services.AddRabbitMqMessaging(rabbitMqOptions);   // publisher + hosted consumers (API process)

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi.yaml", "Recoletas.Booking API v1"));
    app.MapGet("/openapi.yaml", () =>
        Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.yaml"), "application/yaml"))
        .AllowAnonymous();
}
app.UseAuthorization();
app.MapControllers();
// after Build: resolve BookingDatabaseInitializer in a scope and initialize the database
// seedDemoData = app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Booking:SeedDemoData")
```

When invoked, `BookingDatabaseInitializer` applies EF Core migrations. API startup invokes it by
default; Azure deployments disable startup initialization with `Booking:ApplyMigrationsOnStartup`
and execute the same container with `--migrate-only` before deploying the HTTP revision. This mode
seeds only the test-type catalog and exits without HTTP or hosted workers. Schema upgrades must
remain compatible with the running revision; image rollback does not reverse migrations.

Liveness (`/health/live`) and readiness (`/health/ready`) are anonymous infrastructure probes.
Readiness checks the cached RabbitMQ connection and PostgreSQL reachability; dependency failures
return 503 without details. The health-check adapter lives in Infrastructure. Cloud deployments
retain at least one API replica for background work and use certificate-validated AMQPS.
See [Azure deployment](../azure-deployment.md) for provisioning and CI/CD configuration.

The initializer seeds the three synthetic demo
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
In Development, Swagger UI loads that checked-in file directly from `/openapi.yaml`.

`AppointmentSummary`, returned by `GET /appointments` and successful hold confirmation/offer
acceptance, contains scheduled slot `startsAtUtc`/`endsAtUtc` alongside `confirmedAtUtc`. Scheduled
times come from the slot; confirmation time comes from the reservation. These are UTC timestamps
with distinct meanings, and adding the schedule to the response requires no schema change.

Authentication uses HS256 bearer tokens configured through `Jwt:Secret`, `Jwt:Issuer`, and
`Jwt:Audience`; signature, issuer, audience, expiration, and algorithm are validated. Local demo
settings are supplied through .NET User Secrets (see [JWT demo guide](../jwt-auth.md)). The fallback authorization policy requires a
`patient` role and a valid `patientId` GUID claim. Only `POST /patients` opts out of authentication.
`GET /admin/appointments` uses an explicit admin-only policy and requires `from` and `to`; its
application read port joins current confirmed reservations, slots, and patient names without
exposing NHC or changing the schema. Admin tokens must not carry a `patientId` claim, keeping
patient and admin identities mutually exclusive.

### Patient pending confirmations

`GET /appointments/pending` is protected by the existing patient fallback policy and obtains the
patient ID only from validated claims. The controller sends `QueryPendingAppointments`; its
handler captures `IClock.UtcNow` once and uses `ISlotRepository.ListPendingForPatientAsync`.
Infrastructure performs one no-tracking, patient-scoped SQL query, excluding expired leases and
ended slot windows, then orders by expiry, slot start, and slot ID. The handler maps the selected
slots to `PendingAppointmentSummary` records; no Domain entities are exposed.

The response contains `slotId`, `testType`, `startsAtUtc`, `endsAtUtc`, `kind` (`hold`/`offer`),
`generation`, and `expiresAtUtc`, or an empty array. It is not cached (`Cache-Control: no-store`).
There are no history, pagination, caller-selected patient, or date-filter parameters. A started
slot remains eligible until its end, matching the existing mutation rules.

This read does not expire slots, extend leases, change waitlists, enqueue messages, or depend on
RabbitMQ. Its response is a snapshot: the existing hold-confirm and offer-accept transitions remain
the authority and can return 409 after expiry or a generation/state change. No schema or DI change
is introduced, and confirmed-appointment queries remain unchanged.

## 6. Concurrency Model

Slot exclusivity is enforced **in the database**, never in memory:

```sql
UPDATE slots
SET state = 'Held', patient_id = @patientId, expires_at = @expiresAt, generation = generation + 1
WHERE id = @slotId AND state = 'Available'
  AND (expires_at IS NULL OR expires_at <= @now);
-- rows affected == 0 → someone else claimed it
```

- Conditional updates on the single slot row guard every competing claim; no separate unique
  live-claim index is configured. A unique filtered index protects the current reservation pointer.
- The same conditional-update pattern guards offer acceptance (`generation` + `expires_at` match).
- `PostgresConstraintViolationDetector` maps unique-violation errors to a `409 Conflict` result.

### Waitlist entry and release-event concurrency

Slot exclusivity alone does not prevent two different slots from selecting the same waitlist entry.
The offer transaction must exclusively claim that entry, guard the slot transition with the release
event's expected generation, and persist OfferCreated together. If the slot transition fails, the
claim must not commit. The entry-level guarantee does not prohibit different waitlist entries or
appointments for the same patient.

The expected generation travels from SlotReleased into OfferNextWaiter and its conditional database
update. A generation check before invoking the use case is not sufficient: the slot may change
between that read and the write. FIFO selection is not a promise of transport ordering across
concurrent consumers and retries.

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
event in the **same transaction**. `OutboxProcessorHostedService` polls and awaits
`RabbitMqDispatcher` publishing with persistent messages, mandatory routing, and publisher confirms.
Only then does it mark the outbox record processed. No broker calls occur in handlers/HTTP requests. A
`HoldConfirmationRequested` event produces a simulated “please confirm before expiry” notification only
when the current slot remains Held, assigned to the same patient, at the same generation, and unexpired.

- **At-least-once** delivery. Slot-release state transitions use state/generation checks; notification
  effects may repeat after a crash between publication and marking processed, forwarding and ack,
  or sending a notification and ack. There is no inbox or exactly-once claim.
- **Per-slot `generation`** guarding — stale events are ignored; retries do not guarantee FIFO.
- Outbox retry with exponential backoff → PostgreSQL **dead state** after a bounded count.
- Durable direct exchange `booking.events`: `SlotReleased` → `booking.workflow`;
  `HoldConfirmationRequested`, `OfferCreated`, `AppointmentConfirmed` → `booking.notifications`.
- Durable quorum queues; each source queue has `.retry` (5-second TTL and at-least-once
  dead-lettering back to source) and `.dlq`. Consumers ack after handling or confirmed persistent
  forwarding; 5 application handling attempts by default. Failed forwarding/ack closes the channel
  and requeues the original. Reconnection is explicit with a 5-second delay. Repeated infrastructure
  failures can exceed the handling budget; there is no implicit broker delivery limit on source queues.
- Workflow consumers retain state/generation checks and call `OfferNextWaiter`; its existing handler
  atomically transitions the slot and enqueues `OfferCreated`. Notification consumers retain the
  existing sender flow; expired/stale holds and offers are suppressed.
- Structured outbox and notification delivery logs include event ID and type, never patient ID, NHC,
  recipient, subject, or notification body.
- Notification delivery logs include the safe structured outcome (`Succeeded`); hold and offer
  notification logs also include `Generation`, passed as optional metadata in `NotificationRequest`
  for hold confirmation or offer acceptance.

Publisher and consumers remain hosted in the API, with per-delivery DI scopes for EF repositories.
The dispatcher port (`IEventDispatcher`) isolates RabbitMQ from ApplicationServices and Domain.
Infrastructure deserializes and dispatches incoming events; typed application handlers own
workflow and notification state/expiry decisions. Application handlers do not consume Infrastructure
outbox storage types. RabbitMQ channels, ACK/retry/DLQ mechanics, and concrete senders remain adapters.

Outbox completion/failure updates match the lease acquisition identity so a stale worker cannot
overwrite a newer owner's state. Lease loss is explicit. Fencing protects bookkeeping, not
exactly-once publication or external notification effects. `OutboxRecord.LeaseToken` is a nullable
UUID added by `20261114094732_OutboxLeaseFencing`; each acquisition passes its token through
`OutboxMessage.LeaseToken`. Completion/failure requires both a matching token and unexpired
`LeaseUntilUtc`. The lease lasts five minutes, and processing remains sequential without renewal;
an expired acquisition must be recovered rather than recorded as successfully completed.

The explicit wire identifiers and routing keys are `booking.hold-confirmation-requested.v1`,
`booking.slot-released.v1`, `booking.offer-created.v1`, and `booking.appointment-confirmed.v1`.
Only these versioned identifiers are supported. New broker topologies bind only these routing keys;
removing bindings from the declaration does not remove previously created bindings from an existing broker.

See `doc/adr/0001-message-broker-adoption.md` and README for configuration and DLQ replay guidance.

### Test-host and database isolation

E2E hosts use the Testing environment with per-factory configuration, not process-wide environment
changes. PostgreSQL setup distinguishes recognized dependency unavailability from schema, migration,
and authentication errors, which must fail the test.

Most database tests use the shared dedicated test database selected by
`ConnectionStrings__BookingTests`. FIFO, waitlist E2E, outbox-lease, and pending-state scenarios instead create
`bookingtest_<scenario>_<guid>` databases on the same configured server, migrate them, and seed the
test-type catalog. They need isolated waitlist/outbox populations and must not consume or delete
another test's entries. These databases are retained; repeat runs accumulate scenario databases
rather than resetting a shared or application database.

Booking-flow E2E explicitly invokes the application handler for the persisted event. Live RabbitMQ
delivery is covered separately by the opt-in transport test on an empty dedicated vhost.

## 8. Tripwires

- No business logic in controllers — only `_mediator.Send()`.
- No EF types (`DbContext`, `DbSet`, `IQueryable<T>`) outside Infrastructure.
- No in-memory locks as the source of truth for slot exclusivity.
- No `IDomainEvent` marker — Domain owns its event records, and ApplicationServices maps them to
  primitive integration event records in Contracts.
- UTC everywhere + injectable `IClock`.
- No `Thread.Sleep`, `.Result`, `.Wait()`, or `async void`.
- All code, comments, and artifacts in English.

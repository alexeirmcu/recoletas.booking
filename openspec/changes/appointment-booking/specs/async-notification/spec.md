# Async Notification Specification

## Purpose

Defines how hold-confirmation and slot-release notifications are persisted and delivered through a transactional outbox, RabbitMQ confirmed publishing, hosted consumers, guarded workflow transitions, and pluggable senders.

## Requirements

### Requirement: Atomic Notification Persistence

When a slot is held or released, the system MUST persist the corresponding notification event in the same transaction as the state change that produced it. There MUST be no window where the state has changed but the event is not recorded. A successful direct hold MUST enqueue a `HoldConfirmationRequested` Contracts event; a rejected hold MUST NOT enqueue one.

#### Scenario: Release persists event atomically

- GIVEN a slot release (cancel or hold expiry)
- WHEN the release is committed
- THEN the notification event is recorded in the same transaction

#### Scenario: Successful hold persists confirmation request atomically

- GIVEN a free slot and an existing patient
- WHEN the hold transition succeeds
- THEN a `HoldConfirmationRequested` event containing its event ID, slot, patient, test type, generation, expiry, and occurrence time is recorded in the same transaction
- AND a rejected hold records no confirmation request

### Requirement: At-Least-Once Dispatch

A background worker MUST publish outbox events at-least-once using persistent mandatory RabbitMQ messages with `EventId` as `MessageId`. It MUST mark the outbox record processed only after publisher confirmation. Nacks, unroutable returns, timeouts, and connection failures MUST follow the existing outbox retry path. Duplicate workflow delivery MUST NOT produce two active offers; external notification effects MAY repeat with the same MessageId. No exactly-once or DB inbox guarantee is provided.

#### Scenario: Publication is not confirmed

- GIVEN a leased outbox event
- WHEN publication fails or has not yet been confirmed
- THEN the outbox record is not marked processed
- AND a publication failure records an outbox retry

#### Scenario: Duplicate dispatch produces no double offer

- GIVEN a notification event already dispatched and a duplicate delivery
- WHEN the duplicate is processed
- THEN no second offer is created

### Requirement: Outbox Lease Ownership

Each outbox lease acquisition MUST have an ownership identity. Completion and failure updates MUST
match that acquisition and MUST NOT overwrite a newer owner's state. An expired acquisition MUST
NOT be treated as valid merely because its worker still holds the message in memory. Publication
confirmation remains necessary but does not grant ownership of a lease.

#### Scenario: An old worker finishes after lease recovery

- GIVEN a message whose expired lease has been acquired by another worker
- WHEN the original worker records success or failure
- THEN the update is rejected without changing the new owner's state
- AND lease loss is surfaced explicitly

### Requirement: Stable Event Contracts

Persisted event identifiers and routing keys MUST be explicit, stable wire identifiers rather than
CLR type names inferred with `nameof`. The consumer MUST support the existing unversioned event
names as compatibility aliases. Unsupported identifiers MUST fail explicitly. EventId/MessageId
MUST remain unchanged across publication and retry.

#### Scenario: Previously persisted event is dispatched after an upgrade

- GIVEN an outbox record or queued message using an existing unversioned event name
- WHEN the upgraded publisher or consumer processes it
- THEN it resolves to the corresponding supported event contract
- AND its original event ID is preserved

### Requirement: Retry and Dead-Letter

The system MUST retry failed dispatches with backoff and, after retries are exhausted, MUST move the event to a dead-letter state rather than silently dropping it.

The API host MUST run workflow and notification consumers with per-delivery scopes and manual acknowledgments. Durable direct exchange `booking.events` MUST route `SlotReleased` to `booking.workflow` and the three notification events to `booking.notifications`. Each durable quorum source queue MUST have a `.retry` queue (default 5-second TTL with at-least-once dead-lettering back to the source) and a `.dlq`. Handling failures MUST use a bounded attempt budget (default 5), forwarding persistently with mandatory routing and confirms before acknowledging the original. If forwarding or acknowledgment fails, closing the channel MUST leave the original eligible for redelivery. Infrastructure failures/crashes MAY exceed the application retry budget. Outbox publication retries remain separate.

#### Scenario: Consumer failure reaches attempt limit

- GIVEN a delivery that has exhausted its handling attempts
- WHEN handling fails again
- THEN it is forwarded to the source queue's DLQ with its MessageId and Type preserved
- AND the original is acknowledged only after forwarding is confirmed

#### Scenario: Failure retries then dead-letters

- GIVEN a notification that keeps failing to dispatch
- WHEN retries are exhausted
- THEN the event is moved to dead-letter and is not lost

### Requirement: Pluggable Notification Sender

Notification delivery MUST go through a pluggable sender abstraction. At minimum, the system SHALL support a log sender and a webhook-simulation sender.

#### Scenario: Delivery via pluggable sender

- GIVEN a dispatchable notification
- WHEN the worker dispatches it
- THEN it is delivered through the configured sender (log or webhook simulation)

### Requirement: Per-Slot Ordering via Generation

Notification processing MUST enforce per-slot ordering using the slot's generation counter. Events carrying a stale generation MUST be ignored.

These are state/generation guards, not a transport FIFO guarantee; retry deliveries may be reordered.
`SlotReleased` handling MUST call the existing `OfferNextWaiter` use case only for the matching
available slot generation, preserving its transactional `OfferCreated` outbox path. The expected
generation MUST be carried into the conditional database transition; a preliminary read is not
sufficient to enforce this guarantee.

Hold/offer expiry equality MUST compare UTC instants at microsecond precision, matching persisted
PostgreSQL timestamps. The extra 100-nanosecond digit retained in an event MUST NOT make an otherwise
matching lease stale. A different microsecond MUST be rejected, and a notification MUST NOT be sent
once either the persisted expiry or event expiry has elapsed.

#### Scenario: Stale event is ignored

- GIVEN an event with a generation older than the slot's current generation
- WHEN the event is processed
- THEN it is ignored and has no effect

#### Scenario: Slot generation changes after initial event validation

- GIVEN a release event that matched a previously read slot generation
- WHEN the slot advances to another generation before the offer transition
- THEN the conditional transition rejects the old generation
- AND no waitlist claim or OfferCreated event is committed for that delivery

#### Scenario: Hold confirmation for a stale or expired hold is ignored

- GIVEN a `HoldConfirmationRequested` event whose patient or generation no longer matches the current held slot, or whose hold has expired
- WHEN the event is dispatched
- THEN no notification is sent

#### Scenario: Expired or stale waitlist offer is ignored

- GIVEN an `OfferCreated` event whose patient, generation, or expiry no longer matches the offered slot, or whose offer has expired
- WHEN the notification consumer handles it
- THEN no notification is sent

#### Scenario: An active lease event retains sub-microsecond precision

- GIVEN a current hold or offer whose persisted expiry matches the event at microsecond precision
- AND the event retains an additional fractional tick that PostgreSQL did not persist
- WHEN the notification consumer handles it before both expiries
- THEN the notification is sent
- AND expiry differences at microsecond precision still suppress the notification

### Requirement: Exclusive Waitlist Entry Claim

Offering a slot MUST exclusively claim the selected active waitlist entry within the same
transaction as the slot transition and OfferCreated outbox event. Competing consumers offering
different slots MUST NOT consume the same entry. A failed slot transition MUST NOT permanently
consume the waitlist entry.

#### Scenario: Concurrent offers compete for one waitlist entry

- GIVEN one active waitlist entry and two available slots of its test type
- WHEN two consumers concurrently attempt to offer those slots
- THEN at most one committed offer uses that entry
- AND the committed offer and its outbox event are atomic

### Requirement: Application-Owned Event Handling

ApplicationServices MUST own event-driven business decisions using typed integration events.
Infrastructure MUST own transport deserialization, RabbitMQ channels, acknowledgments, retry/DLQ,
and concrete notification adapters. Application event handlers MUST NOT depend on Infrastructure
outbox storage types or RabbitMQ client types.

#### Scenario: Transport delegates business handling

- GIVEN a supported integration event received from RabbitMQ
- WHEN the Infrastructure consumer deserializes it
- THEN it invokes a typed application handler
- AND transport acknowledgment remains conditional on successful handling or confirmed retry/DLQ forwarding

### Requirement: Privacy-Safe Message Tracing

Outbox dispatch and simulated notification delivery logs MUST include structured event ID and event type fields. Simulated notification delivery logs MUST include a safe structured delivery outcome. Outbox failure logs MUST include only the exception type and retry/dead-letter disposition, not the exception object or message. Logs MUST NOT include patient ID, NHC, recipient, notification subject, notification body, or serialized payload.

Simulated hold and offer notification logs MUST additionally include the event's structured
`Generation`, which the patient submits when confirming the hold or accepting the offer.
This metadata MUST NOT require logging the
recipient or notification body.

#### Scenario: Message logs correlate delivery without patient data

- GIVEN an outbox event delivered through a simulated sender
- WHEN dispatch and delivery are logged
- THEN the logs contain structured event ID and event type fields
- AND simulated notification delivery logs contain a safe delivery outcome
- AND outbox failure logs contain the exception type and retry/dead-letter disposition without exception content
- AND the logs contain none of the patient's identifiers or notification content

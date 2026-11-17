# ADR-0001: RabbitMQ Behind the Transactional Outbox

- **Status:** Accepted — implemented first iteration (RMQ-OUTBOX-001)
- **Date:** 2026-11-14
- **Scope:** slot-release workflow and hold/offer/appointment notifications

## Context

The transactional outbox atomically persists integration events with booking state in PostgreSQL.
The previous `PollingDispatcher` mixed waitlist orchestration with notification delivery. RabbitMQ
decouples those consumers without moving publication into handlers or the HTTP request path.

## Decision

Retain the outbox and its leasing, exponential retry, and dead-state behavior. The existing
`OutboxProcessorHostedService` invokes Infrastructure's `RabbitMqDispatcher` through the unchanged
ApplicationServices `IEventDispatcher` port. The dispatcher publishes persistent JSON with `EventId`
as `MessageId`, explicit contract identifiers, mandatory routing, and async publisher
confirmation tracking. It returns only after a broker ack; nack, unroutable return, timeout, or
connection failure throws into the existing outbox failure path. Only then can the processor mark a
row processed. Booking state and event remain in the same `BookingDbContext` transaction.

Publisher and consumers run in the **existing API host**, not a new Worker executable.
Domain/ApplicationServices remain broker-independent. A shared connection uses separate
confirm-enabled channels; publisher channels are short-lived per dispatch for demo simplicity.
Consumers create a DI scope per delivery and use prefetch 1 per queue/channel.

Infrastructure owns RabbitMQ reception, deserialization, acknowledgments, retry/DLQ handling, and
concrete notification senders. ApplicationServices owns typed event handling and the state/expiry
decisions; it does not depend on RabbitMQ or the Infrastructure outbox serializer. Hosting those
components in the API does not move business decisions into the HTTP layer.

```text
Booking transaction -> PostgreSQL outbox -> polling publisher -> booking.events (direct)
                              mark processed after confirm         |
                         +-----------------------------------------+
                         |                                         |
                 booking.workflow                        booking.notifications
                 SlotReleased                            HoldConfirmationRequested
                         |                               OfferCreated / AppointmentConfirmed
             state + generation guards                             |
                 OfferNextWaiter                         current state/expiry guards
                         |                               INotificationSender (log/webhook)
              existing transaction +
              OfferCreated outbox event
```

### Topology and retries

- Exchange `booking.events` and all queues are durable; queues are quorum queues.
- `SlotReleased` binds to `booking.workflow`; `HoldConfirmationRequested`, `OfferCreated`, and
  `AppointmentConfirmed` bind to `booking.notifications`.
- Each source queue has a `.retry` queue with a configurable TTL (default 5 seconds),
  default-exchange dead-letter routing back to the source, `at-least-once` dead-letter strategy,
  and `reject-publish` overflow. Each also has a `.dlq` for exhausted deliveries.
- Consumers ack only after successful handling, including intentional stale-event suppression.
  On handling failure, `booking-retry-count` is incremented and the message is forwarded
  persistently and mandatorily to `.retry` or, after 5 total handling attempts, `.dlq`.
  The original is acked only after forwarding is confirmed.
- If forwarding/ack or connection fails, the channel is disposed and unacknowledged deliveries
  are requeued. Consumers reconnect after 5 seconds. Source quorum queues disable the implicit
  broker delivery limit so infrastructure redeliveries cannot silently discard messages.
  Repeated crashes and infrastructure failures can exceed the application attempt budget.
- PostgreSQL outbox publish retries/dead state are independent of consumer retries/DLQs.
  DLQ replay is manual after inspection and repair: preserve MessageId/Type and reset
  `booking-retry-count`. There is no replay endpoint or automatic DLQ replay.

### Stable event identifiers and rollout

Event types and routing keys are explicit wire identifiers:

| Contract | Type / routing key |
|---|---|
| HoldConfirmationRequested | `booking.hold-confirmation-requested.v1` |
| SlotReleased | `booking.slot-released.v1` |
| OfferCreated | `booking.offer-created.v1` |
| AppointmentConfirmed | `booking.appointment-confirmed.v1` |

Only these versioned identifiers are supported; unversioned class names fail explicitly. Fresh
broker topologies bind only the versioned keys. RabbitMQ does not remove existing bindings when
they are omitted from a declaration: if a broker was previously used with unversioned bindings,
remove those bindings or provision a fresh dedicated vhost before using this version. Any existing
unversioned outbox records and queued messages are unsupported; inspect them before removing or
recreating broker resources, since deleting queues or a vhost discards their messages.

### Delivery semantics

This is **at-least-once**, not exactly-once. A crash after publish confirmation but before outbox
marking, or after retry forwarding but before ack, can duplicate delivery. Existing state/generation
checks and conditional transactional updates prevent duplicate active offers. Hold notifications
require the same patient/generation/expiry and a still-active hold; offer notifications likewise
require a current, unexpired offer. Appointment confirmation retains its existing sender behavior.

Hold/offer expiry equality is evaluated in UTC at microsecond precision, matching PostgreSQL
timestamps. JSON events can retain an additional 100-nanosecond digit; losing that digit during
persistence must not suppress a current notification. A different microsecond still rejects the
event, and both the persisted expiry and event expiry must be later than the current clock. This
does not introduce an expiry grace period.

For slot-release workflow, the event's expected generation must reach the conditional database
update, not just an earlier read. Claiming the waitlist entry, transitioning the slot, and recording
OfferCreated share one transaction. Competing slots cannot consume the same waitlist entry; a
failed slot transition must not commit a claim.

Outbox completion/failure is fenced by lease acquisition ownership. A worker that loses its lease
cannot overwrite the state recorded by a later owner. This protects outbox bookkeeping, not
exactly-once publication: a broker confirmation and the PostgreSQL update are still separate steps.
The additive `20261114094732_OutboxLeaseFencing` migration adds nullable UUID `LeaseToken`.
Acquisitions carry a token and five-minute expiry; completion/failure requires a matching token and
an unexpired lease. Batches remain sequential without renewal, so lease expiry can cause recovery
and duplicate publication rather than extending ownership implicitly.

Notification effects can repeat after sending but before ack. The same EventId/MessageId is passed
to `NotificationRequest`; downstream real providers should support idempotency keys. No DB inbox or
exactly-once guarantee is added. Generations suppress stale events; there is no global/per-slot FIFO claim across
concurrent processing or retries, and no new ordering guarantee for AppointmentConfirmed.

## Operations and tradeoffs

Compose adds RabbitMQ management with a persistent volume, AMQP port 5672 and management port 15672.
`booking_demo` / `booking_demo` are **synthetic local-demo credentials only**. Committed settings
contain empty credential placeholders; configure `RabbitMq__UserName` / `RabbitMq__Password` via
environment or user-secrets. README lists settings, topology, and replay guidance.

Changing immutable queue arguments such as retry TTL requires a planned queue migration; declarations
fail explicitly on incompatible existing topology. This local single-node broker is not HA.
Production needs protected broker access/TLS, distinct credentials, a quorum cluster, capacity
planning, and DLQ monitoring. Broker outages do not block booking commits; publisher failures are
visible and can eventually exhaust the existing outbox retry budget.

RabbitMQ is selected for work-queue routing and retry/DLQ semantics. Kafka/Redis Streams are not
introduced. A separate Worker host, provider-specific deduplication/inbox, and analytics consumers
are future decisions, not part of this iteration.

# ADR-0001: Adopting a Message Broker for Async Notifications

- **Status:** Proposed (documented for future evolution — not implemented in the demo)
- **Date:** 2026-11-12
- **Scope:** waitlist slot-release notifications (brief point 5)

## Context

The demo uses a **transactional outbox** + **background polling worker** + a simulated
`INotificationSender`. This solves the dual-write problem (slot state change and notification
must be atomic) with zero external services, keeping single-command startup.

That design is deliberately demo-scale. It is a single process with one consumer and bounded
throughput. We document here when and how to introduce a real message broker.

## Decision

Introduce a broker when any of the following becomes true:

- Multiple **independent consumers** need the same events (notification + analytics + audit).
- Multiple **instances** of the worker run concurrently (redundancy, throughput, or a network of
  hospitals where each center publishes events).
- **Throughput** exceeds what one SQLite-polling worker can drain.
- We need **event retention / replay** or cross-service fan-out beyond notification.

**The outbox stays.** The broker is introduced *behind* the outbox, not instead of it — the
outbox remains the atomic commit boundary between the database and the messaging system.

## Broker selection (guidance, to be confirmed against real load)

| Broker | Fit | Tradeoffs |
|---|---|---|
| **RabbitMQ** | Work-queue semantics, per-queue FIFO, retries + DLX, mature ops | Single node or quorum queues; less suited to event streaming/replay |
| **Kafka** | High-throughput log, replay from offset, partitioned ordering, retention | More operational weight; ordering only within a partition (key by slot id) |
| **Redis Streams** | Lightweight, already common as a cache | Weaker durability/ops story for critical healthcare notifications |

Preferred default: **RabbitMQ** for work-queue notification (matches FIFO-per-slot + retry + DLX).
Choose **Kafka** if the system grows into an event-sourced/streaming platform.

## Evolution path

```
today:  outbox ── poll worker ── INotificationSender (log/webhook)

future: outbox ── publisher (worker) ── broker ── consumer(s) ── INotificationSender
                                           └── DLQ / retry topics
```

1. Replace the poll-and-dispatch worker with a **publisher** that reads the outbox and publishes
   to the broker, marking rows processed only after the broker acknowledges.
2. Add **consumer** hosts. Notification becomes one consumer; analytics/audit can be added
   without touching the booking service.
3. Configure **retries** and a **dead-letter** queue at the broker level (or keep the
   outbox-level dead-letter as the durable record).

## Delivery guarantees (preserved and answered for README point c)

| Failure | Demo behavior | Broker behavior |
|---|---|---|
| **Lost message** | outbox row is durable; retry | broker acks; un-acked message is redelivered |
| **Duplicate** | dedup by message ID | at-least-once redelivery → consumer dedup by message ID |
| **Out of order** | per-slot generation ignored if stale | partition key = slot id (Kafka) / per-slot FIFO queue (RabbitMQ) |

The contract remains **at-least-once, idempotent consumer, generation-ordered per slot**. The
broker does not change semantics; it moves the same guarantees to a horizontally scalable
transport.

## What changes in code

- `Booking.Infrastructure`: add a `BrokerPublisher` implementing the existing outbox dispatcher port.
- New consumer host project (or a hosted service) consuming events.
- `INotificationSender` implementations unchanged — the seam already isolates delivery.
- No domain changes: the slot state machine and idempotency logic are transport-agnostic.

## Tradeoffs / open questions

- RabbitMQ vs Kafka is deferred until scale, ordering, and retention requirements are measurable.
- Exactly-once delivery is **not** assumed: healthcare side effects must stay idempotent
  regardless of transport.

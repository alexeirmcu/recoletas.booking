# Async Notification Specification

## Purpose

Defines how hold-confirmation and slot-release notifications are persisted and delivered reliably through a transactional outbox, an at-least-once worker, idempotent handling, and pluggable senders.

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

A background worker MUST dispatch outbox events at-least-once. Duplicate dispatch of the same event MUST be idempotent — a message delivered twice MUST NOT produce two offers.

#### Scenario: Duplicate dispatch produces no double offer

- GIVEN a notification event already dispatched and a duplicate delivery
- WHEN the duplicate is processed
- THEN no second offer is created

### Requirement: Retry and Dead-Letter

The system MUST retry failed dispatches with backoff and, after retries are exhausted, MUST move the event to a dead-letter state rather than silently dropping it.

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

#### Scenario: Stale event is ignored

- GIVEN an event with a generation older than the slot's current generation
- WHEN the event is processed
- THEN it is ignored and has no effect

#### Scenario: Hold confirmation for a stale or expired hold is ignored

- GIVEN a `HoldConfirmationRequested` event whose patient or generation no longer matches the current held slot, or whose hold has expired
- WHEN the event is dispatched
- THEN no notification is sent

### Requirement: Privacy-Safe Message Tracing

Outbox dispatch and simulated notification delivery logs MUST include structured event ID and event type fields. Simulated notification delivery logs MUST include a safe structured delivery outcome. Outbox failure logs MUST include only the exception type and retry/dead-letter disposition, not the exception object or message. Logs MUST NOT include patient ID, NHC, recipient, notification subject, notification body, or serialized payload.

#### Scenario: Message logs correlate delivery without patient data

- GIVEN an outbox event delivered through a simulated sender
- WHEN dispatch and delivery are logged
- THEN the logs contain structured event ID and event type fields
- AND simulated notification delivery logs contain a safe delivery outcome
- AND outbox failure logs contain the exception type and retry/dead-letter disposition without exception content
- AND the logs contain none of the patient's identifiers or notification content

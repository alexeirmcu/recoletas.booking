# Slot Booking Specification

## Purpose

Defines the hold / confirm / cancel lifecycle of a slot and the concurrency guarantees that ensure a slot is assigned to at most one patient even under concurrent requests.

## Requirements

### Requirement: Hold a Free Slot

The system MUST allow a patient to place a temporary hold on a free slot. A hold is a temporary reservation that expires after a configurable TTL (default 5 minutes). The hold MUST be recorded in UTC using an injectable clock.

#### Scenario: Hold a free slot

- GIVEN a free slot and a patient identifier
- WHEN the patient requests a hold
- THEN the slot becomes `HELD` and is reserved for that patient until the TTL elapses

#### Scenario: Successful hold requests asynchronous patient confirmation

- GIVEN a free slot and a patient identifier
- WHEN the hold succeeds
- THEN the system raises a `HoldConfirmationRequested` event with the slot, patient, test type, generation, expiry, and occurrence time
- AND the event is persisted in the outbox in the same transaction as the held slot state
- AND notification delivery does not block the HTTP request

#### Scenario: Hold on a non-free slot is rejected

- GIVEN a slot already `HELD`, `CONFIRMED`, or `OFFERED`
- WHEN another patient requests a hold
- THEN the hold is rejected and the slot state is unchanged
- AND no hold-confirmation event is recorded

### Requirement: Confirm a Held Slot

The system MUST allow a patient to confirm their own hold, transitioning the slot to `CONFIRMED` and creating an appointment.

Confirmed appointment responses MUST include the slot's scheduled `startsAtUtc` and `endsAtUtc`
in addition to the reservation's `confirmedAtUtc`, all in UTC. This applies to patient appointment
lookup and successful hold confirmation or offer acceptance. Confirmation time MUST NOT be used
as a substitute for the scheduled appointment window.

#### Scenario: Confirm a held slot

- GIVEN a slot held by a patient within the TTL
- WHEN the same patient confirms
- THEN the slot becomes `CONFIRMED` and the appointment is created

#### Scenario: Confirm after expiry is rejected

- GIVEN a slot whose hold TTL has elapsed
- WHEN the patient attempts to confirm
- THEN the confirmation is rejected and the slot is treated as available

### Requirement: Discover Pending Patient Confirmations

`GET /appointments/pending` MUST return the authenticated patient's current holds and waitlist
offers in one unpaginated JSON array. Patient identity MUST come only from validated JWT claims.
Missing/invalid credentials MUST return 401; an authenticated admin token MUST return 403.
The query MUST NOT return another patient's items.

Each item MUST contain exactly `slotId`, `testType`, `startsAtUtc`, `endsAtUtc`, `kind`,
`generation`, and `expiresAtUtc`. `kind` MUST be `hold` or `offer`, dates MUST be UTC, and all
fields MUST be non-null. Results MUST be sorted by expiry, then slot start, then slot ID.
No matches MUST return 200 with `[]`. The response MUST include `Cache-Control: no-store`.

Eligibility MUST be evaluated using one injected UTC clock value: the slot MUST be Held with
HoldExpiresAtUtc > now or Offered with OfferExpiresAtUtc > now, and EndsAtUtc MUST be > now.
Null/elapsed expiry, Available/Confirmed state, and ended slot windows MUST be excluded.
A started but not ended slot MUST NOT be excluded solely because its start has passed.

The read MUST filter in the database and MUST NOT extend leases, expire slots, change waitlists,
write outbox events, or send notifications. It MUST NOT depend on notification delivery or RabbitMQ.
`GET /appointments` MUST retain its existing confirmed-only behavior.

#### Scenario: Patient discovers a hold and an offer

- GIVEN a patient with a current hold and a current waitlist offer
- WHEN that patient requests their pending confirmations
- THEN both items are returned with their matching kind, current generation, and expiry
- AND another patient's holds or offers are absent

#### Scenario: Expiration is visible without worker processing

- GIVEN a Held or Offered slot whose lease expiry or slot end is equal to or before now
- AND the expiry worker has not transitioned it yet
- WHEN its patient queries pending confirmations
- THEN the slot is absent
- AND the query does not change persisted state

#### Scenario: Patient has no pending confirmations

- GIVEN a patient without eligible held or offered slots
- WHEN that patient queries pending confirmations
- THEN the response is 200 with an empty array

#### Scenario: Patient submits the discovered generation

- GIVEN a pending item returned with its current generation
- WHEN the patient submits that generation to the existing hold-confirm or offer-accept route
- AND the corresponding lease and slot remain valid
- THEN the existing confirmation transition succeeds
- AND the item no longer appears in pending confirmations

#### Scenario: Pending state changes after discovery

- GIVEN a previously returned pending item
- WHEN its lease expires or its state/generation changes before confirmation or acceptance
- THEN the existing guarded POST rejects the action with 409
- AND the earlier GET does not extend or guarantee that lease

### Requirement: Cancel a Confirmed Appointment

The system MUST allow a patient to cancel a confirmed appointment, releasing the slot back to `AVAILABLE`.

#### Scenario: Cancel frees the slot

- GIVEN a confirmed appointment on a slot
- WHEN the patient cancels
- THEN the slot returns to `AVAILABLE`

### Requirement: Patient History Is Retained

The system MUST retain patient rows and appointment history. A patient record MAY carry a nullable `DeletedAtUtc` timestamp for logical deletion, but the system MUST NOT physically delete a patient. Patient foreign keys from `Slots.AssignedPatientId` and `Reservations.PatientId` MUST use restrictive delete behavior. `Reservations.SlotId` MUST also reference `Slots.Id` restrictively. `Slots.CurrentReservationId` MUST retain its unique non-null index but MUST NOT reference `Reservations.Id`, avoiding a circular FK with `Reservations.SlotId`.

#### Scenario: Physical patient deletion is blocked

- GIVEN a patient referenced by a slot, reservation, or waitlist entry
- WHEN a physical delete is attempted
- THEN PostgreSQL rejects the delete and preserves all booking and history rows

#### Scenario: Patient logical deletion timestamp is nullable and persisted

- GIVEN a patient record
- WHEN its nullable `DeletedAtUtc` value is persisted
- THEN the timestamp is stored in UTC without deleting the patient or its history

### Requirement: Exclusive Assignment Under Concurrency

The system MUST guarantee that a slot is assigned to at most one patient, even under concurrent requests. Concurrent holds MUST NOT double-assign a slot.

#### Scenario: Two concurrent holds — exactly one wins

- GIVEN one free slot and two patients holding it concurrently
- WHEN both hold requests are processed
- THEN exactly one hold succeeds and the other is rejected

#### Scenario: Expired hold appears available

- GIVEN a slot whose hold TTL has elapsed
- WHEN the slot is queried or held again
- THEN it is treated as `AVAILABLE` (lazy validity), without waiting for a worker

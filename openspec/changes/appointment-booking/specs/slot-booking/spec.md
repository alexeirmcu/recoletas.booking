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

#### Scenario: Confirm a held slot

- GIVEN a slot held by a patient within the TTL
- WHEN the same patient confirms
- THEN the slot becomes `CONFIRMED` and the appointment is created

#### Scenario: Confirm after expiry is rejected

- GIVEN a slot whose hold TTL has elapsed
- WHEN the patient attempts to confirm
- THEN the confirmation is rejected and the slot is treated as available

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

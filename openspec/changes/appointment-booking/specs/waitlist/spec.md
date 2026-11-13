# Waitlist Specification

## Purpose

Defines a per-test-type waitlist that, when a slot is released, offers it asynchronously to the first waiter through an exclusive, time-limited lease with a generation counter.

## Requirements

### Requirement: Join the Waitlist

The system MUST allow a patient to join the waitlist for one of the catalog test types `BloodTest`, `Ultrasound`, or `MRI`. A patient MUST NOT hold more than one entry per test type. Waitlist entries MUST reference both their patient and test type through restrictive foreign keys.

#### Scenario: Join the waitlist

- GIVEN a patient and a test type
- WHEN the patient joins the waitlist
- THEN they are added in FIFO order

#### Scenario: Unsupported test type is rejected

- GIVEN a patient submits a type not in the closed catalog
- WHEN the join request is validated
- THEN the request is rejected with HTTP 400 and the `unsupported_test_type` validation detail code

#### Scenario: One entry per patient per test type

- GIVEN a patient already on a test-type waitlist
- WHEN the patient joins again
- THEN no duplicate entry is created

### Requirement: Offer Released Slot to First Waiter

When a slot is released (by cancellation or hold expiry), the system MUST asynchronously offer the slot to the first waiter for that test type. The offer MUST be an exclusive, time-limited lease with a limited acceptance window (default 5 minutes, configurable) and MUST carry a generation counter.

#### Scenario: Release offers slot to first waiter

- GIVEN a released slot and a non-empty waitlist
- WHEN the release is processed
- THEN the first waiter receives an exclusive offer with an acceptance window

#### Scenario: Empty waitlist returns slot to available

- GIVEN a released slot and an empty waitlist
- WHEN the release is processed
- THEN the slot returns to `AVAILABLE`

### Requirement: FIFO Advancement on Timeout

The system MUST advance to the next waiter in FIFO order when an offer's acceptance window elapses without acceptance.

#### Scenario: Offer timeout advances FIFO

- GIVEN an offered slot whose acceptance window has elapsed
- WHEN the timeout is processed
- THEN the offer expires and the slot is offered to the next waiter in FIFO order

### Requirement: Offer Acceptance Is Race-Safe

The system MUST resolve an accept-versus-expiry race so that exactly one outcome wins: either the waiter accepts within the window and claims the slot, or the offer expires and the slot moves on.

#### Scenario: Accept versus expiry — exactly one wins

- GIVEN an offer near its acceptance deadline
- WHEN acceptance and expiry are processed concurrently
- THEN exactly one of them succeeds; the other has no effect

#### Scenario: Offer is exclusive

- GIVEN an exclusive offer held by one waiter
- WHEN another patient attempts to claim the same slot
- THEN the attempt is rejected

# Slot Agenda Specification

## Purpose

Defines the slot model and how free slots are queried for a diagnostic-test appointment service. A slot is a reservable unit of availability, defined by a test type and a datetime window, that carries a lifecycle state.

## Requirements

### Requirement: Slot Model

The system SHALL model a slot as the combination of a test type and a datetime window (a start and end instant), together with a lifecycle state. The state MUST be one of: `AVAILABLE`, `HELD`, `CONFIRMED`, or `OFFERED`. A slot's test type MUST reference the closed catalog containing exactly `BloodTest`, `Ultrasound`, and `MRI`. A slot whose window end instant has passed SHALL be considered EXPIRED and MUST NOT be bookable.

#### Scenario: Slot is available

- GIVEN a slot with a future datetime window
- WHEN the slot is not held, confirmed, or offered
- THEN the slot is in the `AVAILABLE` state

#### Scenario: Slot window has passed

- GIVEN a slot whose window end instant is in the past
- WHEN the agenda is queried for that window
- THEN the slot is EXPIRED and is never returned as free

### Requirement: Free Slot Query

The system MUST expose a query for free slots that is filterable by test type and by a date range. The query MUST return only slots in the `AVAILABLE` state whose datetime window is still in the future; slots in `HELD`, `CONFIRMED`, or `OFFERED` state MUST be excluded.

#### Scenario: Query free slots

- GIVEN three available slots across a date range
- WHEN a client queries free slots for that range
- THEN all three slots are returned

#### Scenario: Filter by test type

- GIVEN available slots for multiple test types
- WHEN a client queries free slots filtered by one test type
- THEN only slots of that test type are returned

#### Scenario: Date range filtering

- GIVEN available slots inside and outside a date range
- WHEN a client queries free slots for that range
- THEN only slots within the range are returned

#### Scenario: Unsupported test type filter is rejected

- GIVEN a client supplies a test type outside the supported catalog
- WHEN the agenda query is validated
- THEN the request is rejected with HTTP 400 and the `unsupported_test_type` validation detail code

#### Scenario: Non-available slots are excluded

- GIVEN slots in `HELD`, `CONFIRMED`, and `OFFERED` states
- WHEN a client queries free slots
- THEN those slots are NOT returned

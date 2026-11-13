# Patient Lifecycle Specification

## Purpose

Defines patient retention and the referential-integrity rules that preserve appointment, waitlist, and slot history.

## Requirements

### Requirement: Patient Records Are Retained

The system MUST retain patient rows and related booking history. `Patient` MUST include nullable `DeletedAtUtc` for logical-deletion metadata. This change MUST NOT add a physical-delete operation or patient deletion endpoint. Patient references from `Slots.AssignedPatientId`, `Reservations.PatientId`, and `WaitlistEntries.PatientId` MUST use restrictive delete behavior; `Reservations.SlotId` MUST reference `Slots.Id` restrictively.

#### Scenario: Referenced patient cannot be physically deleted

- GIVEN a patient referenced by slot, reservation, or waitlist data
- WHEN a physical delete is attempted
- THEN the database rejects the operation and retains all related rows

#### Scenario: Deletion timestamp is nullable

- GIVEN a patient record without logical-deletion metadata
- WHEN the record is persisted
- THEN `DeletedAtUtc` remains null

#### Scenario: Logical deletion timestamp is persisted

- GIVEN a patient record with a UTC deletion timestamp
- WHEN the record is persisted and reloaded
- THEN the same UTC timestamp is returned and the patient/history rows remain

### Requirement: Optional NHC Is Unique

The system MUST allow NHC to be omitted. When supplied, the system MUST trim surrounding whitespace before persistence; blank or whitespace-only values MUST be treated as null. PostgreSQL MUST enforce uniqueness for non-null NHC values with a filtered unique index so concurrent registrations cannot persist duplicates, while multiple patients with null NHC remain allowed. A duplicate registration MUST return HTTP 409 with the stable error code `patient_nhc_already_exists`. The migration MUST normalize existing values and fail with a diagnostic if duplicates remain after normalization; it MUST NOT delete or merge patient records.

#### Scenario: NHC is normalized before persistence

- GIVEN a patient registration with an NHC containing surrounding whitespace
- WHEN the patient is registered
- THEN the stored and returned NHC contains only the trimmed value
- AND a whitespace-only NHC is stored as null

#### Scenario: Duplicate NHC is rejected

- GIVEN a patient already exists with a non-null NHC
- WHEN another patient is registered with the same trimmed NHC
- THEN registration returns HTTP 409 with error code `patient_nhc_already_exists`
- AND no second patient record is created

#### Scenario: Multiple patients may omit NHC

- GIVEN multiple patient registrations with null or blank NHC
- WHEN those patients are persisted
- THEN each patient is stored successfully with a null NHC

#### Scenario: Concurrent duplicate registrations remain unique

- GIVEN two registrations for the same trimmed non-null NHC arrive concurrently
- WHEN PostgreSQL commits the registrations
- THEN at most one patient is created with that NHC
- AND the rejected registration returns HTTP 409 with error code `patient_nhc_already_exists`

#### Scenario: Existing duplicate NHC values block migration safely

- GIVEN existing patient rows whose NHC values are duplicates after trimming
- WHEN the uniqueness migration runs
- THEN migration fails with a diagnostic identifying the duplicate patient rows
- AND no patient rows are deleted or merged

### Requirement: Slot Reservation Pointer Avoids a Circular Foreign Key

The system MUST retain the unique filtered index on `Slots.CurrentReservationId` but MUST NOT add an FK from that column to `Reservations.Id`. `Reservations.SlotId` MUST be the one-way FK to `Slots.Id`, avoiding a circular relationship.

#### Scenario: Reservation references its slot without a reverse FK

- GIVEN a reservation referencing an existing slot
- WHEN the persistence schema is applied
- THEN `Reservations.SlotId` is restrictive and `Slots.CurrentReservationId` remains an indexed pointer without an FK

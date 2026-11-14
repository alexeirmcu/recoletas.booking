---
name: api-rest-standards
description: "Trigger: API, Endpoint, REST, Controller, HTTP response. Enforce consistency between the Recoletas.Booking REST API and its OpenAPI specification."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract
Activate this skill when adding endpoints, modifying request/response models, or implementing error handling.

## Hard Rules
- **Source of Truth**: the checked-in `doc/openapi.yaml` is the definitive contract; keep it aligned with the controllers.
- **Response Consistency**: use the Contracts Models (`SlotSummary`, `AppointmentSummary`, `PatientSummary`) as response shapes.
- **Error Codes**:
  - `400 Bad Request`: validation failures (FluentValidation).
  - `401 Unauthorized`: missing/invalid bearer token or required identity claims.
  - `403 Forbidden`: authenticated token does not have the role required by the endpoint.
  - `404 Not Found`: entity not found.
  - `409 Conflict`: slot already taken, offer expired, or generation mismatch.
- **Controller Thinness**: no business logic; only `_mediator.Send()`.
- **Authorization**: patient routes require the `patient` role and `patientId` claim; the admin appointment read requires `admin`. Patient registration and infrastructure liveness/readiness probes are anonymous; probes expose no dependency details.

## Decision Gates
| Action | Response Code |
|--------|---------------|
| Resource created / hold confirmed | `200 OK` (returning the result object) |
| Resource not found | `404 Not Found` |
| Business rule violation | `409 Conflict` |

## Execution Steps
1. Define the endpoint in the appropriate controller.
2. Create/update the Contracts Models or ApiModel records.
3. Implement the MediatR command + handler.
4. Ensure the response matches the Swagger spec.

## Output Contract
- Endpoints adhering to the OpenAPI spec.
- Structured error responses via exception-handling middleware.

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
- **Source of Truth**: the OpenAPI spec (generated via Swashbuckle from the controllers) is the definitive contract.
- **Response Consistency**: use the Contracts Models (`SlotSummary`, `AppointmentSummary`, `PatientSummary`) as response shapes.
- **Error Codes**:
  - `400 Bad Request`: validation failures (FluentValidation).
  - `401 Unauthorized`: missing/invalid `X-Patient-Id`.
  - `404 Not Found`: entity not found.
  - `409 Conflict`: slot already taken, offer expired, or generation mismatch.
- **Controller Thinness**: no business logic; only `_mediator.Send()`.

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

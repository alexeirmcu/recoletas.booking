using MediatR;
using Microsoft.AspNetCore.Mvc;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.API.Controllers;

[ApiController]
public sealed class AppointmentsController(IMediator mediator, IPatientIdentityResolver patientIdentity)
    : ControllerBase
{
    [HttpPost("holds/{id:guid}/confirm")]
    public async Task<IActionResult> ConfirmHold(Guid id, [FromBody] ConfirmHoldRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConfirmHold(id, patientIdentity.GetRequiredPatientId(),
            request), cancellationToken);
        if (result.Succeeded)
        {
            return Ok(result.Appointment);
        }

        return result.ErrorCode == BookingErrorCodes.SlotNotFound
            ? NotFound(new { error = result.ErrorCode })
            : Conflict(new { error = result.ErrorCode });
    }

    [HttpPost("appointments/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelAppointment(id, patientIdentity.GetRequiredPatientId()),
            cancellationToken);
        if (result.Succeeded)
        {
            return NoContent();
        }

        return NotFound(new { error = result.ErrorCode });
    }

    [HttpGet("appointments")]
    public async Task<IActionResult> GetAppointments(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new QueryAppointments(patientIdentity.GetRequiredPatientId()), cancellationToken));
}

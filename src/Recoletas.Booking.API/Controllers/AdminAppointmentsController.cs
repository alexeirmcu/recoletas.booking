using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;

namespace Recoletas.Booking.API.Controllers;

[ApiController]
[Authorize(Policy = BookingPolicies.AdminOnly)]
public sealed class AdminAppointmentsController(IMediator mediator) : ControllerBase
{
    [HttpGet("admin/appointments")]
    public async Task<IActionResult> GetConfirmedAppointments(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (from is null || to is null)
        {
            return BadRequest(new { error = BookingErrorCodes.FromAndToRequired });
        }

        var appointments = await mediator.Send(
            new QueryAdminAppointments(from.Value, to.Value),
            cancellationToken);
        return Ok(appointments);
    }
}

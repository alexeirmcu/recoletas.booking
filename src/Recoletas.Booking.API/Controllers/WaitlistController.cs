using MediatR;
using Microsoft.AspNetCore.Mvc;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.API.Controllers;

[ApiController]
[Route("")]
public sealed class WaitlistController(IMediator mediator, IPatientIdentityResolver patientIdentity)
    : ControllerBase
{
    [HttpPost("waitlist")]
    public async Task<IActionResult> Join([FromBody] JoinWaitlistRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new JoinWaitlist(patientIdentity.GetRequiredPatientId(), request),
            cancellationToken);
        if (result.Joined)
        {
            return Ok(result);
        }

        return result.ErrorCode == BookingErrorCodes.PatientNotFound
            ? NotFound(new { error = result.ErrorCode })
            : Conflict(new { error = result.ErrorCode });
    }

    [HttpPost("offers/{id:guid}/accept")]
    public async Task<IActionResult> AcceptOffer(Guid id, [FromBody] AcceptOfferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AcceptOffer(id, patientIdentity.GetRequiredPatientId(), request),
            cancellationToken);
        if (result.Succeeded)
        {
            return Ok(result.Appointment);
        }

        return result.ErrorCode == BookingErrorCodes.SlotNotFound
            ? NotFound(new { error = result.ErrorCode })
            : Conflict(new { error = result.ErrorCode });
    }

    [HttpPost("offers/{id:guid}/decline")]
    public async Task<IActionResult> DeclineOffer(Guid id, [FromBody] DeclineOfferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeclineOffer(id, patientIdentity.GetRequiredPatientId(), request),
            cancellationToken);
        return result.Succeeded ? NoContent() : Conflict(new { error = result.ErrorCode });
    }
}

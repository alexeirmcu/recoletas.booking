using MediatR;
using Microsoft.AspNetCore.Mvc;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;

namespace Recoletas.Booking.API.Controllers;

[ApiController]
[Route("slots")]
public sealed class SlotsController(IMediator mediator, IPatientIdentityResolver patientIdentity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSlots([FromQuery] string? testType, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (from is null || to is null)
        {
            return BadRequest(new { error = BookingErrorCodes.FromAndToRequired });
        }

        return Ok(await mediator.Send(new QueryAgenda(testType, from.Value, to.Value), cancellationToken));
    }

    [HttpPost("{id:guid}/hold")]
    public async Task<IActionResult> Hold(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new BlockSlot(id, patientIdentity.GetRequiredPatientId()), cancellationToken);
        if (result.Succeeded)
        {
            return Ok(result);
        }

        return result.ErrorCode is BookingErrorCodes.SlotNotFound or BookingErrorCodes.PatientNotFound
            ? NotFound(new { error = result.ErrorCode })
            : Conflict(new { error = result.ErrorCode });
    }
}

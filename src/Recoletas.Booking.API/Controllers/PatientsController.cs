using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.API.Controllers;

[ApiController]
[Route("patients")]
public sealed class PatientsController(IMediator mediator) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> CreatePatient([FromBody] RegisterPatientRequest request,
        CancellationToken cancellationToken) => Ok(await mediator.Send(new RegisterPatient(request), cancellationToken));
}

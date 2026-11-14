using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class RegisterPatientHandler(IPatientRepository patients, IClock clock)
    : IRequestHandler<RegisterPatient, PatientSummary>
{
    public async Task<PatientSummary> Handle(RegisterPatient request, CancellationToken cancellationToken)
    {
        var patient = new Patient(PatientId.New(), request.Request.Name, clock.UtcNow.ToUniversalTime(),
            request.Request.Nhc);
        try
        {
            await patients.AddAsync(patient, cancellationToken);
        }
        catch (DuplicatePatientNhcException)
        {
            throw new PatientNhcAlreadyExistsException();
        }

        return new PatientSummary(patient.Id.Value, patient.Name, patient.Nhc, patient.CreatedAtUtc);
    }
}

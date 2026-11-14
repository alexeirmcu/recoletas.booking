using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Abstractions;

public interface IPatientRepository
{
    Task<Patient?> GetByIdAsync(PatientId id, CancellationToken cancellationToken);

    Task AddAsync(Patient patient, CancellationToken cancellationToken);
}

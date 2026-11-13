using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Repository;

public interface IAppointmentRepository
{
    Task<IReadOnlyList<Reservation>> ListForPatientAsync(PatientId patientId,
        CancellationToken cancellationToken);
}

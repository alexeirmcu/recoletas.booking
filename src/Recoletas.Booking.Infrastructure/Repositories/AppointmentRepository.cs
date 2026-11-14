using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.Repositories;

public sealed class AppointmentRepository(BookingDbContext dbContext) : IAppointmentRepository
{
    public async Task<IReadOnlyList<Reservation>> ListForPatientAsync(PatientId patientId,
        CancellationToken cancellationToken) =>
        await dbContext.Reservations.AsNoTracking()
            .Where(reservation => reservation.PatientId == patientId)
            .OrderByDescending(reservation => reservation.ConfirmedAtUtc)
            .ToListAsync(cancellationToken);
}

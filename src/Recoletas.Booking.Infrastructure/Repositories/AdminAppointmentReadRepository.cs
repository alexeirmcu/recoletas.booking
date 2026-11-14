using Microsoft.EntityFrameworkCore;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;

namespace Recoletas.Booking.Infrastructure.Repositories;

public sealed class AdminAppointmentReadRepository(BookingDbContext dbContext) : IAdminAppointmentReadRepository
{
    public async Task<IReadOnlyList<AdminAppointmentSummary>> ListConfirmedAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from slot in dbContext.Slots.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on slot.CurrentReservationId equals reservation.Id
            join patient in dbContext.Patients.AsNoTracking()
                on reservation.PatientId equals patient.Id
            where slot.State == SlotState.Confirmed &&
                  slot.AssignedPatientId == reservation.PatientId &&
                  slot.StartsAtUtc >= fromUtc &&
                  slot.StartsAtUtc < toUtc
            orderby slot.StartsAtUtc
            select new { Slot = slot, Reservation = reservation, Patient = patient })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new AdminAppointmentSummary(
                row.Reservation.Id,
                row.Slot.Id.Value,
                row.Slot.TestType.Value,
                row.Slot.StartsAtUtc,
                row.Slot.EndsAtUtc,
                row.Reservation.ConfirmedAtUtc,
                row.Reservation.PatientId.Value,
                row.Patient.Name))
            .ToArray();
    }
}

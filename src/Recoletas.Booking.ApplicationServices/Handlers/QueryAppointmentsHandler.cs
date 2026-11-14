using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class QueryAppointmentsHandler(IAppointmentRepository appointments, ISlotRepository slots)
    : IRequestHandler<QueryAppointments, IReadOnlyList<AppointmentSummary>>
{
    public async Task<IReadOnlyList<AppointmentSummary>> Handle(QueryAppointments request,
        CancellationToken cancellationToken)
    {
        var reservations = await appointments.ListForPatientAsync(new PatientId(request.PatientId),
            cancellationToken);
        var results = new List<AppointmentSummary>(reservations.Count);
        foreach (var reservation in reservations)
        {
            var slot = await slots.GetByIdAsync(reservation.SlotId, cancellationToken);
            if (slot is not null && slot.State == SlotState.Confirmed &&
                slot.CurrentReservationId == reservation.Id)
            {
                results.Add(new AppointmentSummary(reservation.Id, reservation.SlotId.Value,
                    reservation.PatientId.Value, slot.TestType.Value, slot.StartsAtUtc, slot.EndsAtUtc,
                    reservation.ConfirmedAtUtc));
            }
        }

        return results;
    }
}

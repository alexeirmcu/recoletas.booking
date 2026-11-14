using MediatR;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class QueryPendingAppointmentsHandler(ISlotRepository slots, IClock clock)
    : IRequestHandler<QueryPendingAppointments, IReadOnlyList<PendingAppointmentSummary>>
{
    public async Task<IReadOnlyList<PendingAppointmentSummary>> Handle(QueryPendingAppointments request,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var pendingSlots = await slots.ListPendingForPatientAsync(new PatientId(request.PatientId), now,
            cancellationToken);
        return pendingSlots.Select(Map).ToArray();
    }

    private static PendingAppointmentSummary Map(Slot slot)
    {
        var (kind, expiresAtUtc) = slot.State switch
        {
            SlotState.Held when slot.HoldExpiresAtUtc is { } holdExpiry => ("hold", holdExpiry),
            SlotState.Offered when slot.OfferExpiresAtUtc is { } offerExpiry => ("offer", offerExpiry),
            _ => throw new InvalidOperationException("The pending slot has no active hold or offer expiry.")
        };

        return new PendingAppointmentSummary(slot.Id.Value, slot.TestType.Value, slot.StartsAtUtc, slot.EndsAtUtc,
            kind, slot.Generation, expiresAtUtc.ToUniversalTime());
    }
}

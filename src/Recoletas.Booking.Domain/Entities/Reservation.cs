using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Entities;

public sealed record Reservation(Guid Id, SlotId SlotId, PatientId PatientId, DateTimeOffset ConfirmedAtUtc)
{
    public static Reservation Create(SlotId slotId, PatientId patientId, DateTimeOffset confirmedAtUtc) =>
        new(Guid.NewGuid(), slotId, patientId, confirmedAtUtc.ToUniversalTime());
}

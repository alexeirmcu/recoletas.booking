namespace Recoletas.Booking.Contracts.Events;

public sealed record AppointmentConfirmed(Guid EventId, Guid SlotId, Guid PatientId, Guid ReservationId,
    string TestType, DateTimeOffset OccurredAtUtc);

namespace Recoletas.Booking.Contracts.Models;

public sealed record AppointmentSummary(Guid ReservationId, Guid SlotId, Guid PatientId, string TestType,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, DateTimeOffset ConfirmedAtUtc);

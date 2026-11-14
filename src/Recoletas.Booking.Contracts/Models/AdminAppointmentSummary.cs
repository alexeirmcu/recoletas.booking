namespace Recoletas.Booking.Contracts.Models;

public sealed record AdminAppointmentSummary(
    Guid ReservationId,
    Guid SlotId,
    string TestType,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset ConfirmedAtUtc,
    Guid PatientId,
    string PatientName);

namespace Recoletas.Booking.Contracts.Models;

public sealed record PendingAppointmentSummary(
    Guid SlotId,
    string TestType,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string Kind,
    int Generation,
    DateTimeOffset ExpiresAtUtc);

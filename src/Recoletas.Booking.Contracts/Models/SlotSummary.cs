namespace Recoletas.Booking.Contracts.Models;

public sealed record SlotSummary(Guid SlotId, string TestType, DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc, string State);

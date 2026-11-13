namespace Recoletas.Booking.Contracts.Events;

public sealed record SlotReleased(Guid EventId, Guid SlotId, string TestType, int Generation,
    DateTimeOffset OccurredAtUtc);

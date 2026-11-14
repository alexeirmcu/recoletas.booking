namespace Recoletas.Booking.Contracts.Models;

public sealed record OutboxMessage(Guid Id, string EventType, string Payload, DateTimeOffset OccurredAtUtc,
    Guid LeaseToken = default);

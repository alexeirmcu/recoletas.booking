namespace Recoletas.Booking.Contracts.Events;

public sealed record HoldConfirmationRequested(Guid EventId, Guid SlotId, Guid PatientId, string TestType,
    int Generation, DateTimeOffset ExpiresAtUtc, DateTimeOffset OccurredAtUtc);

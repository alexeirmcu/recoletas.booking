namespace Recoletas.Booking.Domain.Events;

public abstract record SlotDomainEvent;

public sealed record HoldPlaced(Guid EventId, Guid SlotId, Guid PatientId, string TestType, int Generation,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset OccurredAtUtc) : SlotDomainEvent;

public sealed record AppointmentConfirmedEvent(Guid EventId, Guid SlotId, Guid PatientId, Guid ReservationId,
    string TestType, DateTimeOffset OccurredAtUtc) : SlotDomainEvent;

public sealed record OfferPlaced(Guid EventId, Guid SlotId, Guid PatientId, string TestType, int Generation,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset OccurredAtUtc) : SlotDomainEvent;

public sealed record SlotReleasedEvent(Guid EventId, Guid SlotId, string TestType, int Generation,
    DateTimeOffset OccurredAtUtc) : SlotDomainEvent;

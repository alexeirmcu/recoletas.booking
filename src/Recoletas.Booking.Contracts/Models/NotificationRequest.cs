namespace Recoletas.Booking.Contracts.Models;

public sealed record NotificationRequest(Guid MessageId, string EventType, string Recipient, string Subject,
    string Message);

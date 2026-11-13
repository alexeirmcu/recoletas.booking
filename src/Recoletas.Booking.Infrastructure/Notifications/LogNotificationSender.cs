using Recoletas.Booking.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.Infrastructure.Notifications;

public sealed class LogNotificationSender(ILogger<LogNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Notification delivery {DeliveryOutcome} for event {EventId} of type {EventType}.",
            "Succeeded", notification.MessageId, notification.EventType);
        return Task.CompletedTask;
    }
}

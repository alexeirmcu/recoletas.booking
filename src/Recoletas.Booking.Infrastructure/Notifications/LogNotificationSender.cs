using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Messaging;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.Infrastructure.Notifications;

public sealed class LogNotificationSender(ILogger<LogNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (notification.Generation is int generation)
        {
            logger.LogInformation(
                "Notification delivery {DeliveryOutcome} for event {EventId} of type {EventType} with generation {Generation}.",
                "Succeeded", notification.MessageId, notification.EventType, generation);
        }
        else
        {
            logger.LogInformation("Notification delivery {DeliveryOutcome} for event {EventId} of type {EventType}.",
                "Succeeded", notification.MessageId, notification.EventType);
        }
        return Task.CompletedTask;
    }
}

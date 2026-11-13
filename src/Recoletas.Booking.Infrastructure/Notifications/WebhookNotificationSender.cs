using Recoletas.Booking.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.Infrastructure.Notifications;

/// <summary>Simulates webhook delivery without making network calls in the demo service.</summary>
public sealed class WebhookNotificationSender(ILogger<WebhookNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Simulated webhook delivery {DeliveryOutcome} for event {EventId} of type {EventType}.",
            "Succeeded", notification.MessageId, notification.EventType);
        return Task.CompletedTask;
    }
}

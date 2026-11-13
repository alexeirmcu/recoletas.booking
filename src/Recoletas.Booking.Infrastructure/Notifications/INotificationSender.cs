using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.Infrastructure.Notifications;

public interface INotificationSender
{
    Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken);
}

using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Messaging;

public interface INotificationSender
{
    Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken);
}

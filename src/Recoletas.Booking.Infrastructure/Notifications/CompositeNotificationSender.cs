using Recoletas.Booking.Contracts.Models;
namespace Recoletas.Booking.Infrastructure.Notifications;

public sealed class CompositeNotificationSender(IEnumerable<INotificationSender> senders) : INotificationSender
{
    private readonly IReadOnlyList<INotificationSender> _senders = senders.ToArray();

    public async Task SendAsync(NotificationRequest notification, CancellationToken cancellationToken)
    {
        foreach (var sender in _senders)
        {
            await sender.SendAsync(notification, cancellationToken);
        }
    }
}

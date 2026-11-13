using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Messaging;

public interface IEventDispatcher
{
    Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken);
}

using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Contracts.Events;

namespace Recoletas.Booking.ApplicationServices.Handlers;

internal static class OutboxEvents
{
    internal static async Task EnqueuePendingAsync(Slot slot, IOutboxStore outbox,
        CancellationToken cancellationToken)
    {
        foreach (var integrationEvent in slot.DrainDomainEvents())
        {
            switch (integrationEvent)
            {
                case HoldConfirmationRequested holdConfirmation:
                    await outbox.EnqueueAsync(holdConfirmation, cancellationToken);
                    break;
                case SlotReleased released:
                    await outbox.EnqueueAsync(released, cancellationToken);
                    break;
                case OfferCreated offer:
                    await outbox.EnqueueAsync(offer, cancellationToken);
                    break;
                case AppointmentConfirmed confirmed:
                    await outbox.EnqueueAsync(confirmed, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported domain event '{integrationEvent.GetType().Name}'.");
            }
        }
    }
}

using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.Events;
using IntegrationEvents = Recoletas.Booking.Contracts.Events;

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
                case HoldPlaced hold:
                    await outbox.EnqueueAsync(new IntegrationEvents.HoldConfirmationRequested(hold.EventId,
                        hold.SlotId, hold.PatientId, hold.TestType, hold.Generation, hold.ExpiresAtUtc,
                        hold.OccurredAtUtc), cancellationToken);
                    break;
                case SlotReleasedEvent released:
                    await outbox.EnqueueAsync(new IntegrationEvents.SlotReleased(released.EventId,
                        released.SlotId, released.TestType, released.Generation, released.OccurredAtUtc),
                        cancellationToken);
                    break;
                case OfferPlaced offer:
                    await outbox.EnqueueAsync(new IntegrationEvents.OfferCreated(offer.EventId, offer.SlotId,
                        offer.PatientId, offer.TestType, offer.Generation, offer.ExpiresAtUtc, offer.OccurredAtUtc),
                        cancellationToken);
                    break;
                case AppointmentConfirmedEvent confirmed:
                    await outbox.EnqueueAsync(new IntegrationEvents.AppointmentConfirmed(confirmed.EventId,
                        confirmed.SlotId, confirmed.PatientId, confirmed.ReservationId, confirmed.TestType,
                        confirmed.OccurredAtUtc), cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported domain event '{integrationEvent.GetType().Name}'.");
            }
        }
    }
}

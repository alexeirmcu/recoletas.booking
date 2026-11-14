using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class SlotReleasedEventHandler(ISlotRepository slots, IMediator mediator)
{
    public async Task HandleAsync(SlotReleased released, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(released.SlotId), cancellationToken);
        if (slot is null || slot.State != SlotState.Available || slot.Generation != released.Generation)
        {
            return;
        }

        await mediator.Send(new OfferNextWaiter(released.SlotId, released.Generation), cancellationToken);
    }
}

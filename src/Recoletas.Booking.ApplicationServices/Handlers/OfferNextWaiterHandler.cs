using Recoletas.Booking.Contracts.Events;
using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class OfferNextWaiterHandler(ISlotRepository slots, IWaitlistRepository waitlist,
    IOutboxStore outbox, IUnitOfWork unitOfWork, IClock clock, ApplicationServicesOptions options)
    : IRequestHandler<OfferNextWaiter, bool>
{
    public async Task<bool> Handle(OfferNextWaiter request, CancellationToken cancellationToken)
    {
        var window = options.OfferWindow;
        if (window <= TimeSpan.Zero)
        {
            return false;
        }

        var slotId = new SlotId(request.SlotId);
        return await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var slot = await slots.GetByIdAsync(slotId, transactionToken);
            if (slot is null)
            {
                return false;
            }

            var waiter = await waitlist.GetNextAsync(slot.TestType, transactionToken);
            if (waiter is null)
            {
                return false;
            }

            var now = clock.UtcNow.ToUniversalTime();
            var offered = await slots.TryOfferAsync(slotId, waiter.PatientId, now, now.Add(window),
                transactionToken);
            if (offered is null)
            {
                return false;
            }

            await waitlist.RemoveAsync(waiter.Id, transactionToken);
            await OutboxEvents.EnqueuePendingAsync(offered, outbox, transactionToken);
            return true;
        }, cancellationToken);
    }
}

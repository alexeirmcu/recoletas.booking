using Recoletas.Booking.Contracts.Events;
using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Abstractions;
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

            if (request.ExpectedGeneration is not null && slot.Generation != request.ExpectedGeneration)
            {
                return false;
            }

            var now = clock.UtcNow.ToUniversalTime();
            while (true)
            {
                var waiter = await waitlist.GetNextAsync(slot.TestType, transactionToken);
                if (waiter is null)
                {
                    return false;
                }

                if (!await waitlist.TryClaimAsync(waiter.Id, transactionToken))
                {
                    continue;
                }

                var offered = await slots.TryOfferAsync(slotId, waiter.PatientId, now, now.Add(window),
                    transactionToken, request.ExpectedGeneration);
                if (offered is null)
                {
                    if (!await waitlist.TryRestoreClaimAsync(waiter.Id, transactionToken))
                    {
                        throw new InvalidOperationException(
                            $"Waitlist claim '{waiter.Id}' could not be restored after an unsuccessful offer.");
                    }

                    return false;
                }

                await OutboxEvents.EnqueuePendingAsync(offered, outbox, transactionToken);
                return true;
            }
        }, cancellationToken);
    }
}

using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class ExpireSlotLeaseHandler(ISlotRepository slots, IOutboxStore outbox,
    IUnitOfWork unitOfWork, IClock clock) : IRequestHandler<ExpireSlotLease, bool>
{
    public async Task<bool> Handle(ExpireSlotLease request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var slotId = new SlotId(request.SlotId);
            var released = await slots.TryExpireLeaseAsync(slotId, clock.UtcNow.ToUniversalTime(), transactionToken);
            if (released is null)
            {
                return false;
            }

            await OutboxEvents.EnqueuePendingAsync(released, outbox, transactionToken);
            return true;
        }, cancellationToken);
}

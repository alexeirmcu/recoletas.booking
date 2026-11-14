using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class DeclineOfferHandler(ISlotRepository slots, IOutboxStore outbox,
    IUnitOfWork unitOfWork, IClock clock) : IRequestHandler<DeclineOffer, BookingResult>
{
    public async Task<BookingResult> Handle(DeclineOffer request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var released = await slots.TryDeclineOfferAsync(new SlotId(request.SlotId),
                new PatientId(request.PatientId), request.Request.Generation, clock.UtcNow.ToUniversalTime(),
                transactionToken);
            if (released is null)
            {
                return new(false, BookingErrorCodes.OfferInvalidOrExpired);
            }

            await OutboxEvents.EnqueuePendingAsync(released, outbox, transactionToken);
            return new BookingResult(true);
        }, cancellationToken);
}

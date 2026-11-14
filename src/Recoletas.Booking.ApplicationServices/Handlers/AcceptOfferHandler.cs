using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class AcceptOfferHandler(ISlotRepository slots, IWaitlistRepository waitlist,
    IOutboxStore outbox, IUnitOfWork unitOfWork, IClock clock) : IRequestHandler<AcceptOffer, BookingResult>
{
    public async Task<BookingResult> Handle(AcceptOffer request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var slotId = new SlotId(request.SlotId);
            var patientId = new PatientId(request.PatientId);
            var slot = await slots.GetByIdAsync(slotId, transactionToken);
            if (slot is null)
            {
                return new(false, BookingErrorCodes.SlotNotFound);
            }

            var mutation = await slots.TryAcceptOfferAsync(slotId, patientId, request.Request.Generation,
                clock.UtcNow.ToUniversalTime(), transactionToken);
            if (mutation is null)
            {
                return new(false, BookingErrorCodes.OfferInvalidOrExpired);
            }

            await OutboxEvents.EnqueuePendingAsync(mutation.Slot, outbox, transactionToken);
            var entry = await waitlist.GetActiveEntryAsync(patientId, slot.TestType, transactionToken);
            if (entry is not null)
            {
                await waitlist.RemoveAsync(entry.Id, transactionToken);
            }

            return new BookingResult(true, Appointment: new AppointmentSummary(mutation.Value.Id,
                mutation.Value.SlotId.Value, mutation.Value.PatientId.Value, mutation.Slot.TestType.Value,
                mutation.Slot.StartsAtUtc, mutation.Slot.EndsAtUtc, mutation.Value.ConfirmedAtUtc));
        }, cancellationToken);
}

using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class ConfirmHoldHandler(ISlotRepository slots, IOutboxStore outbox, IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ConfirmHold, BookingResult>
{
    public async Task<BookingResult> Handle(ConfirmHold request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var mutation = await slots.TryConfirmAsync(new SlotId(request.SlotId),
                new PatientId(request.PatientId), request.Request.Generation, clock.UtcNow.ToUniversalTime(),
                transactionToken);
            if (mutation is null)
            {
                return new(false, BookingErrorCodes.HoldInvalidOrExpired);
            }

            await OutboxEvents.EnqueuePendingAsync(mutation.Slot, outbox, transactionToken);
            return new BookingResult(true, Appointment: new AppointmentSummary(mutation.Value.Id,
                mutation.Value.SlotId.Value, mutation.Value.PatientId.Value, mutation.Slot.TestType.Value,
                mutation.Slot.StartsAtUtc, mutation.Slot.EndsAtUtc, mutation.Value.ConfirmedAtUtc));
        }, cancellationToken);
}

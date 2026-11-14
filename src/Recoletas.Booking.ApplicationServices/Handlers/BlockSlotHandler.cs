using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class BlockSlotHandler(ISlotRepository slots, IPatientRepository patients, IOutboxStore outbox,
    IUnitOfWork unitOfWork, IClock clock, ApplicationServicesOptions options)
    : IRequestHandler<BlockSlot, BookingResult>
{
    public async Task<BookingResult> Handle(BlockSlot request, CancellationToken cancellationToken)
    {
        var patientId = new PatientId(request.PatientId);
        if (await patients.GetByIdAsync(patientId, cancellationToken) is null)
        {
            return new(false, BookingErrorCodes.PatientNotFound);
        }

        var slotId = new SlotId(request.SlotId);
        if (await slots.GetByIdAsync(slotId, cancellationToken) is null)
        {
            return new(false, BookingErrorCodes.SlotNotFound);
        }

        var now = clock.UtcNow.ToUniversalTime();
        return await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var held = await slots.TryHoldAsync(slotId, patientId, now, now.Add(options.HoldTtl), transactionToken);
            if (held is null)
            {
                return new(false, BookingErrorCodes.SlotUnavailable);
            }

            await OutboxEvents.EnqueuePendingAsync(held, outbox, transactionToken);
            return new BookingResult(true, Generation: held.Generation);
        }, cancellationToken);
    }
}

using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class CancelAppointmentHandler(ISlotRepository slots, IOutboxStore outbox,
    IUnitOfWork unitOfWork, IClock clock) : IRequestHandler<CancelAppointment, BookingResult>
{
    public async Task<BookingResult> Handle(CancelAppointment request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async transactionToken =>
        {
            var released = await slots.TryCancelAsync(request.ReservationId, new PatientId(request.PatientId),
                clock.UtcNow.ToUniversalTime(), transactionToken);
            if (released is null)
            {
                return new(false, BookingErrorCodes.AppointmentNotFound);
            }

            await OutboxEvents.EnqueuePendingAsync(released, outbox, transactionToken);
            return new BookingResult(true);
        }, cancellationToken);
}

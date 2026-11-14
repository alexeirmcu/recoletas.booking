using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Outbox;

namespace Recoletas.Booking.ApplicationServices.Abstractions;

public interface IOutboxStore
{
    Task EnqueueAsync(HoldConfirmationRequested integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(SlotReleased integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(OfferCreated integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(AppointmentConfirmed integrationEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessage>> LeaseAsync(int batchSize, CancellationToken cancellationToken);

    Task<bool> MarkProcessedAsync(Guid messageId, Guid leaseToken, CancellationToken cancellationToken);

    Task<OutboxFailureResult> RecordFailureAsync(Guid messageId, Guid leaseToken, string reason, TimeSpan retryDelay,
        int maxAttempts, CancellationToken cancellationToken);
}

using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.Domain.Repository;

public interface IOutboxStore
{
    Task EnqueueAsync(HoldConfirmationRequested integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(SlotReleased integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(OfferCreated integrationEvent, CancellationToken cancellationToken);

    Task EnqueueAsync(AppointmentConfirmed integrationEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessage>> LeaseAsync(int batchSize, CancellationToken cancellationToken);

    Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken);

    Task MarkDeadAsync(Guid messageId, string reason, CancellationToken cancellationToken);

    Task<bool> RecordFailureAsync(Guid messageId, string reason, TimeSpan retryDelay, int maxAttempts,
        CancellationToken cancellationToken);
}

using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.ApplicationServices.Messaging;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.ApplicationServices.Outbox;

public sealed class OutboxProcessor(IOutboxStore outbox, IEventDispatcher dispatcher,
    OutboxProcessorOptions options, ILogger<OutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var messages = await outbox.LeaseAsync(options.BatchSize, cancellationToken);
        foreach (var message in messages)
        {
            try
            {
                await dispatcher.DispatchAsync(message, cancellationToken);
                await outbox.MarkProcessedAsync(message.Id, cancellationToken);
                logger.LogInformation("Outbox message {EventId} of type {EventType} was processed.",
                    message.Id, message.EventType);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var deadLettered = await outbox.RecordFailureAsync(message.Id, exception.Message,
                    options.RetryBaseDelay, options.MaxAttempts, cancellationToken);
                logger.LogWarning(
                    "Outbox message {EventId} of type {EventType} failed with {FailureType}; it {Disposition}.",
                    message.Id, message.EventType, exception.GetType().Name,
                    deadLettered ? "was dead-lettered" : "will be retried");
            }
        }

        return messages.Count;
    }
}

using Recoletas.Booking.ApplicationServices.Abstractions;
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
                var completed = await outbox.MarkProcessedAsync(message.Id, message.LeaseToken, cancellationToken);
                if (completed)
                {
                    logger.LogInformation("Outbox message {EventId} of type {EventType} was processed.",
                        message.Id, message.EventType);
                }
                else
                {
                    logger.LogWarning(
                        "Outbox message {EventId} of type {EventType} lost its lease before completion.",
                        message.Id, message.EventType);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failureResult = await outbox.RecordFailureAsync(message.Id, message.LeaseToken, exception.Message,
                    options.RetryBaseDelay, options.MaxAttempts, cancellationToken);
                var disposition = failureResult switch
                {
                    OutboxFailureResult.RetryScheduled => "will be retried",
                    OutboxFailureResult.DeadLettered => "was dead-lettered",
                    OutboxFailureResult.LeaseLost => "lost its lease before failure could be recorded",
                    _ => throw new ArgumentOutOfRangeException(nameof(failureResult), failureResult, null)
                };
                logger.LogWarning(
                    "Outbox message {EventId} of type {EventType} failed with {FailureType}; it {Disposition}.",
                    message.Id, message.EventType, exception.GetType().Name,
                    disposition);
            }
        }

        return messages.Count;
    }
}

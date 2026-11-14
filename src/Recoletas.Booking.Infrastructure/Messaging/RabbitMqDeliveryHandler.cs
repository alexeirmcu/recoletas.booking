using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.Infrastructure.Messaging;

public sealed class RabbitMqDeliveryHandler(RabbitMqOptions options, ILogger<RabbitMqDeliveryHandler> logger)
{
    private const string RetryHeader = "booking-retry-count";

    public async Task HandleAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery,
        Func<OutboxMessage, CancellationToken, Task> handle, CancellationToken token)
    {
        try
        {
            var eventType = RabbitMqTopology.RoutingKey(delivery.BasicProperties.Type ?? "");
            var message = new OutboxMessage(Guid.Parse(delivery.BasicProperties.MessageId ?? ""),
                eventType, Encoding.UTF8.GetString(delivery.Body.Span),
                DateTimeOffset.FromUnixTimeSeconds(delivery.BasicProperties.Timestamp.UnixTime));
            await handle(message, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var properties = new BasicProperties(delivery.BasicProperties);
            properties.Persistent = true;
            properties.Headers = properties.Headers is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(properties.Headers);
            var retries = properties.Headers.TryGetValue(RetryHeader, out var count) && count is int value
                ? value : 0;
            var dead = retries >= options.MaxConsumerAttempts - 1;
            properties.Headers[RetryHeader] = retries + 1;
            logger.LogWarning("Consumer event {EventId} of type {EventType} failed with {FailureType}; {Disposition}.",
                delivery.BasicProperties.MessageId, delivery.BasicProperties.Type, exception.GetType().Name,
                dead ? "dead-lettering" : "retrying");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.ConfirmTimeout);
            // Do not ack the original if forwarding fails. Closing the channel requeues it.
            await channel.BasicPublishAsync("", queue + (dead ? ".dlq" : ".retry"), mandatory: true,
                basicProperties: properties, body: delivery.Body, cancellationToken: timeout.Token);
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: token);
    }
}

using System.Text;
using RabbitMQ.Client;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.Infrastructure.Messaging;

public sealed class RabbitMqDispatcher(RabbitMqConnection connection, RabbitMqOptions options) : IEventDispatcher
{
    public async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConfirmTimeout);
        await using var channel = await connection.CreateChannelAsync(timeout.Token);
        await PublishAsync(channel, message, timeout.Token);
    }

    public static async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken token)
    {
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, RabbitMqTopology.RoutingKey(message.EventType),
            mandatory: true, basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = message.Id.ToString(),
                Type = message.EventType,
                ContentType = "application/json",
                Timestamp = new AmqpTimestamp(message.OccurredAtUtc.ToUnixTimeSeconds())
            }, body: Encoding.UTF8.GetBytes(message.Payload), cancellationToken: token);
    }
}

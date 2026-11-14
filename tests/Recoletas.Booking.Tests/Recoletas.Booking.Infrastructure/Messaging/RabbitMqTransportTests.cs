using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Infrastructure.Messaging;

[TestClass]
[TestCategory("BrokerIntegration")]
public sealed class RabbitMqTransportTests
{
    [TestMethod]
    public async Task PublishAndConsume_RealBroker_ConfirmsRoutesRetriesAndDeadLetters()
    {
        var virtualHost = Environment.GetEnvironmentVariable("RabbitMqTests__VirtualHost");
        if (string.IsNullOrWhiteSpace(virtualHost))
        {
            Assert.Inconclusive("Set RabbitMqTests__VirtualHost to a dedicated EMPTY test vhost, plus UserName/Password.");
        }

        var options = new RabbitMqOptions
        {
            HostName = Environment.GetEnvironmentVariable("RabbitMqTests__HostName") ?? "localhost",
            VirtualHost = virtualHost,
            UserName = Environment.GetEnvironmentVariable("RabbitMqTests__UserName") ?? "",
            Password = Environment.GetEnvironmentVariable("RabbitMqTests__Password") ?? "",
            RetryDelay = TimeSpan.FromMilliseconds(100),
            MaxConsumerAttempts = 2
        };
        await using var connection = new RabbitMqConnection(options);
        await using var channel = await connection.CreateChannelAsync(CancellationToken.None);
        var message = new OutboxMessage(Guid.NewGuid(), "SlotReleased", "{}", DateTimeOffset.UtcNow);
        var handler = new RabbitMqDeliveryHandler(options, new CapturingLogger<RabbitMqDeliveryHandler>());

        await RabbitMqDispatcher.PublishAsync(channel, message, CancellationToken.None);
        var original = await GetAsync(channel, RabbitMqTopology.WorkflowQueue);
        await handler.HandleAsync(channel, RabbitMqTopology.WorkflowQueue, ToDelivery(original),
            (_, _) => Task.FromException(new InvalidOperationException("test failure")), CancellationToken.None);
        var retried = await GetAsync(channel, RabbitMqTopology.WorkflowQueue);
        await handler.HandleAsync(channel, RabbitMqTopology.WorkflowQueue, ToDelivery(retried),
            (_, _) => Task.FromException(new InvalidOperationException("test failure")), CancellationToken.None);
        var dead = await GetAsync(channel, RabbitMqTopology.WorkflowQueue + ".dlq");

        Assert.AreEqual(message.Id.ToString(), original.BasicProperties.MessageId);
        Assert.IsTrue(original.BasicProperties.Persistent);
        Assert.AreEqual(message.Id.ToString(), retried.BasicProperties.MessageId);
        Assert.AreEqual(message.Id.ToString(), dead.BasicProperties.MessageId);
        Assert.AreEqual(message.EventType, dead.BasicProperties.Type);
        Assert.AreEqual(2, dead.BasicProperties.Headers!["booking-retry-count"]);
        await channel.BasicAckAsync(dead.DeliveryTag, false);

        var notification = message with { Id = Guid.NewGuid(), EventType = "AppointmentConfirmed" };
        await RabbitMqDispatcher.PublishAsync(channel, notification, CancellationToken.None);
        var received = await GetAsync(channel, RabbitMqTopology.NotificationQueue);
        Assert.AreEqual(notification.Id.ToString(), received.BasicProperties.MessageId);
        await channel.BasicAckAsync(received.DeliveryTag, false);

        await Assert.ThrowsExactlyAsync<PublishReturnException>(async () =>
            await channel.BasicPublishAsync(RabbitMqTopology.Exchange, "no-binding", true,
                new BasicProperties { Persistent = true }, ReadOnlyMemory<byte>.Empty));
    }

    private static BasicDeliverEventArgs ToDelivery(BasicGetResult result) =>
        new("test", result.DeliveryTag, result.Redelivered, result.Exchange, result.RoutingKey,
            result.BasicProperties, result.Body);

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false, timeout.Token);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}

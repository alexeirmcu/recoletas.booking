using System.Text;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Infrastructure.Messaging;

[TestClass]
[TestCategory("Unit")]
public sealed class RabbitMqDeliveryHandlerTests
{
    [TestMethod]
    public async Task Handle_PendingHandling_AcknowledgesOnlyAfterCompletion()
    {
        var channel = new Mock<IChannel>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateHandler();

        var handling = handler.HandleAsync(channel.Object, RabbitMqTopology.WorkflowQueue, CreateDelivery(0),
            (_, _) => completion.Task, CancellationToken.None);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Never);
        completion.SetResult();
        await handling;

        channel.Verify(c => c.BasicAckAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(0, ".retry")]
    [DataRow(4, ".dlq")]
    public async Task Handle_Failure_ForwardsWithConfirmationBeforeAcknowledging(int retries, string suffix)
    {
        var channel = new Mock<IChannel>();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = CreateDelivery(retries);
        channel.Setup(c => c.BasicPublishAsync("", RabbitMqTopology.WorkflowQueue + suffix, true,
                It.Is<BasicProperties>(p => p.Persistent && p.MessageId == delivery.BasicProperties.MessageId &&
                    p.Type == BookingEventTypes.SlotReleased && (int)p.Headers!["booking-retry-count"]! == retries + 1),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(confirmation.Task));
        var handler = CreateHandler();

        var handling = handler.HandleAsync(channel.Object, RabbitMqTopology.WorkflowQueue, delivery,
            (_, _) => Task.FromException(new InvalidOperationException("private payload")), CancellationToken.None);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Never);
        confirmation.SetResult();
        await handling;

        channel.VerifyAll();
        channel.Verify(c => c.BasicAckAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_ForwardingFailure_DoesNotAcknowledgeOriginal()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(Task.FromException(new InvalidOperationException("broker unavailable"))));
        var handler = CreateHandler();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => handler.HandleAsync(channel.Object,
            RabbitMqTopology.WorkflowQueue, CreateDelivery(0),
            (_, _) => Task.FromException(new InvalidOperationException("sender failure")), CancellationToken.None));

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RabbitMqDeliveryHandler CreateHandler() =>
        new(new RabbitMqOptions(), new CapturingLogger<RabbitMqDeliveryHandler>());

    private static BasicDeliverEventArgs CreateDelivery(int retries) => new("consumer", 1, false,
        RabbitMqTopology.Exchange, BookingEventTypes.SlotReleased, new BasicProperties
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = BookingEventTypes.SlotReleased,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?> { ["booking-retry-count"] = retries }
        }, Encoding.UTF8.GetBytes("{}"));
}

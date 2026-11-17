using System.Text;
using Moq;
using RabbitMQ.Client;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.ApplicationServices.Outbox;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Infrastructure.Messaging;

[TestClass]
[TestCategory("Unit")]
public sealed class RabbitMqDispatcherTests
{
    [DataTestMethod]
    [DataRow(BookingEventTypes.SlotReleased)]
    [DataRow(BookingEventTypes.HoldConfirmationRequested)]
    [DataRow(BookingEventTypes.OfferCreated)]
    [DataRow(BookingEventTypes.AppointmentConfirmed)]
    public async Task Publish_KnownEvent_UsesPersistentMandatoryPublicationAndEventId(string eventType)
    {
        var message = new OutboxMessage(Guid.NewGuid(), eventType, "{\"value\":1}", DateTimeOffset.UtcNow);
        var channel = new Mock<IChannel>();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Setup(c => c.BasicPublishAsync(RabbitMqTopology.Exchange, eventType, true,
                It.Is<BasicProperties>(p => p.Persistent && p.MessageId == message.Id.ToString() &&
                    p.Type == eventType && p.ContentType == "application/json"),
                It.Is<ReadOnlyMemory<byte>>(b => Encoding.UTF8.GetString(b.ToArray()) == message.Payload),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(confirmation.Task));

        var publish = RabbitMqDispatcher.PublishAsync(channel.Object, message, CancellationToken.None);
        Assert.IsFalse(publish.IsCompleted);
        confirmation.SetResult();
        await publish;

        channel.VerifyAll();
    }

    [TestMethod]
    public async Task ProcessBatch_PublishConfirmationFailure_RecordsRetryWithoutMarkingProcessed()
    {
        var message = new OutboxMessage(Guid.NewGuid(), BookingEventTypes.SlotReleased, "{}", DateTimeOffset.UtcNow);
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(Task.FromException(new InvalidOperationException("publish nack or return"))));
        var dispatcher = new Mock<IEventDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, CancellationToken>((m, t) => RabbitMqDispatcher.PublishAsync(channel.Object, m, t));
        var store = new Mock<IOutboxStore>();
        store.Setup(s => s.LeaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([message]);
        store.Setup(s => s.RecordFailureAsync(message.Id, message.LeaseToken, It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(OutboxFailureResult.RetryScheduled);
        var processor = new OutboxProcessor(store.Object, dispatcher.Object, new OutboxProcessorOptions(),
            new CapturingLogger<OutboxProcessor>());

        await processor.ProcessBatchAsync(CancellationToken.None);

        store.Verify(s => s.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(s => s.RecordFailureAsync(message.Id, message.LeaseToken, It.IsAny<string>(), It.IsAny<TimeSpan>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ProcessBatch_PendingConfirmation_MarksProcessedOnlyAfterConfirmation()
    {
        var message = new OutboxMessage(Guid.NewGuid(), BookingEventTypes.SlotReleased, "{}", DateTimeOffset.UtcNow);
        var channel = new Mock<IChannel>();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Setup(c => c.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(confirmation.Task));
        var dispatcher = new Mock<IEventDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, CancellationToken>((m, t) => RabbitMqDispatcher.PublishAsync(channel.Object, m, t));
        var store = new Mock<IOutboxStore>();
        store.Setup(s => s.LeaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([message]);
        store.Setup(s => s.MarkProcessedAsync(message.Id, message.LeaseToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var processor = new OutboxProcessor(store.Object, dispatcher.Object, new OutboxProcessorOptions(),
            new CapturingLogger<OutboxProcessor>());

        var processing = processor.ProcessBatchAsync(CancellationToken.None);
        store.Verify(s => s.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        confirmation.SetResult();
        await processing;

        store.Verify(s => s.MarkProcessedAsync(message.Id, message.LeaseToken, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

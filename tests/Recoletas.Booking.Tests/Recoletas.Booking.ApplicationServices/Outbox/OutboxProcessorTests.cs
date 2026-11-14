using Moq;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.ApplicationServices.Outbox;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Outbox;

[TestClass]
[TestCategory("Unit")]
public sealed class OutboxProcessorTests
{
    [TestMethod]
    public async Task ProcessBatch_SuccessfulDispatch_LogsEventCorrelationWithoutPatientData()
    {
        var eventId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var message = new OutboxMessage(eventId, BookingEventTypes.HoldConfirmationRequested,
            $"{{\"patientId\":\"{patientId}\"}}", DateTimeOffset.UtcNow, leaseToken);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.LeaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        outbox.Setup(store => store.MarkProcessedAsync(eventId, leaseToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var dispatcher = new Mock<IEventDispatcher>();
        dispatcher.Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var logger = new CapturingLogger<OutboxProcessor>();
        var processor = new OutboxProcessor(outbox.Object, dispatcher.Object, new OutboxProcessorOptions(), logger);

        var count = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.AreEqual(1, count);
        var log = logger.Entries.Single();
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, log.Level);
        StringAssert.Contains(log.Text, eventId.ToString());
        StringAssert.Contains(log.Text, BookingEventTypes.HoldConfirmationRequested);
        Assert.IsFalse(log.Text.Contains(patientId.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(eventId, log.Properties["EventId"]);
        Assert.AreEqual(BookingEventTypes.HoldConfirmationRequested, log.Properties["EventType"]);
        Assert.IsFalse(log.Properties.ContainsKey("FailureType"));
        Assert.IsFalse(log.Properties.ContainsKey("Disposition"));
        Assert.IsFalse(log.Properties.ContainsKey("Payload"));
        Assert.IsFalse(log.Properties.ContainsKey("PatientId"));
        Assert.IsNull(log.Exception);
    }

    [TestMethod]
    public async Task ProcessBatch_DispatchFailure_LogsEventCorrelationAndRetryDisposition()
    {
        var eventId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        const string privateRecipient = "private-recipient@example.invalid";
        const string privateBody = "PRIVATE PATIENT MESSAGE BODY";
        var message = new OutboxMessage(eventId, BookingEventTypes.HoldConfirmationRequested,
            $"{{\"patientId\":\"{patientId}\",\"recipient\":\"{privateRecipient}\",\"body\":\"{privateBody}\"}}",
            DateTimeOffset.UtcNow, leaseToken);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.LeaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        outbox.Setup(store => store.RecordFailureAsync(eventId, leaseToken, It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboxFailureResult.RetryScheduled);
        var dispatcher = new Mock<IEventDispatcher>();
        dispatcher.Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(privateBody));
        var logger = new CapturingLogger<OutboxProcessor>();
        var processor = new OutboxProcessor(outbox.Object, dispatcher.Object, new OutboxProcessorOptions(), logger);

        await processor.ProcessBatchAsync(CancellationToken.None);

        var log = logger.Entries.Single();
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, log.Level);
        StringAssert.Contains(log.Text, eventId.ToString());
        StringAssert.Contains(log.Text, BookingEventTypes.HoldConfirmationRequested);
        StringAssert.Contains(log.Text, nameof(InvalidOperationException));
        StringAssert.Contains(log.Text, "will be retried");
        Assert.IsFalse(log.Text.Contains(patientId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(log.Text.Contains(privateRecipient, StringComparison.Ordinal));
        Assert.IsFalse(log.Text.Contains(privateBody, StringComparison.Ordinal));
        Assert.AreEqual(eventId, log.Properties["EventId"]);
        Assert.AreEqual(BookingEventTypes.HoldConfirmationRequested, log.Properties["EventType"]);
        Assert.AreEqual(nameof(InvalidOperationException), log.Properties["FailureType"]);
        Assert.AreEqual("will be retried", log.Properties["Disposition"]);
        Assert.IsFalse(log.Properties.ContainsKey("Payload"));
        Assert.IsFalse(log.Properties.ContainsKey("PatientId"));
        Assert.IsFalse(log.Properties.ContainsKey("Recipient"));
        Assert.IsNull(log.Exception);
    }

    [TestMethod]
    public async Task ProcessBatch_DispatchFailureAfterRetries_LogsDeadLetterDisposition()
    {
        var eventId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var message = new OutboxMessage(eventId, BookingEventTypes.HoldConfirmationRequested, "{}", DateTimeOffset.UtcNow,
            leaseToken);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.LeaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        outbox.Setup(store => store.RecordFailureAsync(eventId, leaseToken, It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboxFailureResult.DeadLettered);
        var dispatcher = new Mock<IEventDispatcher>();
        dispatcher.Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("safe test failure"));
        var logger = new CapturingLogger<OutboxProcessor>();
        var processor = new OutboxProcessor(outbox.Object, dispatcher.Object, new OutboxProcessorOptions(), logger);

        await processor.ProcessBatchAsync(CancellationToken.None);

        var log = logger.Entries.Single();
        Assert.AreEqual(BookingEventTypes.HoldConfirmationRequested, log.Properties["EventType"]);
        Assert.AreEqual(nameof(InvalidOperationException), log.Properties["FailureType"]);
        Assert.AreEqual("was dead-lettered", log.Properties["Disposition"]);
        Assert.IsNull(log.Exception);
    }
}

using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Infrastructure.Notifications;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Infrastructure.Notifications;

[TestClass]
[TestCategory("Unit")]
public sealed class NotificationLoggingTests
{
    [TestMethod]
    public async Task SendAsync_SimulatedSendersLogEventCorrelationWithoutPatientData()
    {
        var eventId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        const string nhc = "NHC-DO-NOT-LOG";
        const string recipient = "patient-recipient@example.invalid";
        const string subject = "PRIVATE SUBJECT";
        const string body = "PRIVATE NOTIFICATION BODY";
        var notification = new NotificationRequest(eventId, "HoldConfirmationRequested", recipient, subject,
            $"{body} {patientId} {nhc}");
        var logLogger = new CapturingLogger<LogNotificationSender>();
        var webhookLogger = new CapturingLogger<WebhookNotificationSender>();

        await new LogNotificationSender(logLogger).SendAsync(notification, CancellationToken.None);
        await new WebhookNotificationSender(webhookLogger).SendAsync(notification, CancellationToken.None);

        Assert.AreEqual(1, logLogger.Entries.Count);
        Assert.AreEqual(1, webhookLogger.Entries.Count);
        foreach (var entry in logLogger.Entries.Concat(webhookLogger.Entries))
        {
            StringAssert.Contains(entry.Text, eventId.ToString());
            StringAssert.Contains(entry.Text, "HoldConfirmationRequested");
            Assert.IsFalse(entry.Text.Contains(patientId.ToString(), StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(nhc, StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(recipient, StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(subject, StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(body, StringComparison.Ordinal));
            Assert.IsTrue(entry.Properties.ContainsKey("EventId"));
            Assert.IsTrue(entry.Properties.ContainsKey("EventType"));
            Assert.AreEqual("Succeeded", entry.Properties["DeliveryOutcome"]);
            Assert.IsFalse(entry.Properties.ContainsKey("Recipient"));
            Assert.IsFalse(entry.Properties.ContainsKey("Subject"));
            Assert.IsFalse(entry.Properties.ContainsKey("Message"));
            Assert.IsFalse(entry.Properties.ContainsKey("Generation"));
        }
    }

    [DataTestMethod]
    [DataRow(BookingEventTypes.OfferCreated)]
    [DataRow(BookingEventTypes.HoldConfirmationRequested)]
    public async Task SendAsync_HoldOrOfferNotification_LogsStructuredGenerationWithoutRecipient(string eventType)
    {
        var notification = new NotificationRequest(Guid.NewGuid(), eventType,
            "private-recipient@example.invalid", "PRIVATE SUBJECT", "PRIVATE BODY", Generation: 7);
        var logLogger = new CapturingLogger<LogNotificationSender>();
        var webhookLogger = new CapturingLogger<WebhookNotificationSender>();

        await new LogNotificationSender(logLogger).SendAsync(notification, CancellationToken.None);
        await new WebhookNotificationSender(webhookLogger).SendAsync(notification, CancellationToken.None);

        Assert.AreEqual(1, logLogger.Entries.Count);
        Assert.AreEqual(1, webhookLogger.Entries.Count);
        foreach (var entry in logLogger.Entries.Concat(webhookLogger.Entries))
        {
            Assert.AreEqual(notification.Generation, entry.Properties["Generation"]);
            Assert.AreEqual(notification.MessageId, entry.Properties["EventId"]);
            Assert.AreEqual(notification.EventType, entry.Properties["EventType"]);
            StringAssert.Contains(entry.Text, "generation 7");
            Assert.IsFalse(entry.Text.Contains(notification.Recipient, StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(notification.Subject, StringComparison.Ordinal));
            Assert.IsFalse(entry.Text.Contains(notification.Message, StringComparison.Ordinal));
        }
    }
}

using System.Text.Json;
using MediatR;
using Moq;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.Notifications;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Messaging;

[TestClass]
[TestCategory("Unit")]
public sealed class PollingDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Dispatch_ActiveHoldConfirmationRequest_SendsConfirmationNotification()
    {
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = CreateSlot(slotId);
        Assert.IsTrue(slot.TryHold(patientId, Now, Now.AddMinutes(5)));
        var integrationEvent = new HoldConfirmationRequested(Guid.NewGuid(), slotId.Value, patientId.Value,
            slot.TestType.Value, slot.Generation, slot.HoldExpiresAtUtc!.Value, Now);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var sender = new Mock<INotificationSender>();
        NotificationRequest? request = null;
        sender.Setup(notificationSender => notificationSender.SendAsync(It.IsAny<NotificationRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<NotificationRequest, CancellationToken>((notification, _) => request = notification)
            .Returns(Task.CompletedTask);
        var dispatcher = new PollingDispatcher(slots.Object, Mock.Of<IMediator>(), sender.Object,
            new FakeClock(Now), new CapturingLogger<PollingDispatcher>());

        await dispatcher.DispatchAsync(ToMessage(integrationEvent), CancellationToken.None);

        Assert.IsNotNull(request);
        Assert.AreEqual(integrationEvent.EventId, request!.MessageId);
        Assert.AreEqual(nameof(HoldConfirmationRequested), request.EventType);
        Assert.AreEqual(patientId.Value.ToString(), request.Recipient);
        StringAssert.Contains(request.Message, "confirm your appointment before the hold expires");
        sender.Verify(notificationSender => notificationSender.SendAsync(It.IsAny<NotificationRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task Dispatch_StaleGenerationOrExpiredHold_DoesNotSendNotification(bool staleGeneration,
        bool expired)
    {
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var expiresAt = expired ? Now.AddMinutes(-1) : Now.AddMinutes(5);
        var slot = CreateSlot(slotId);
        Assert.IsTrue(slot.TryHold(patientId, Now.AddMinutes(-10), expiresAt));
        var eventGeneration = staleGeneration ? slot.Generation - 1 : slot.Generation;
        var integrationEvent = new HoldConfirmationRequested(Guid.NewGuid(), slotId.Value, patientId.Value,
            slot.TestType.Value, eventGeneration, expiresAt, Now);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var sender = new Mock<INotificationSender>(MockBehavior.Strict);
        var dispatcher = new PollingDispatcher(slots.Object, Mock.Of<IMediator>(), sender.Object,
            new FakeClock(Now), new CapturingLogger<PollingDispatcher>());

        await dispatcher.DispatchAsync(ToMessage(integrationEvent), CancellationToken.None);

        sender.VerifyNoOtherCalls();
    }

    private static Slot CreateSlot(SlotId id) =>
        new(id, new TestType("BloodTest"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30));

    private static OutboxMessage ToMessage(HoldConfirmationRequested integrationEvent) => new(
        integrationEvent.EventId, nameof(HoldConfirmationRequested),
        JsonSerializer.Serialize(integrationEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        integrationEvent.OccurredAtUtc);
}

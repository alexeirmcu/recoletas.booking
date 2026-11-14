using Moq;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Messaging;

[TestClass]
[TestCategory("Unit")]
public sealed class NotificationEventHandlerTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Handle_ActiveHoldConfirmationRequest_SendsConfirmationNotification()
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
        var dispatcher = new NotificationEventHandler(slots.Object, sender.Object,
            new FakeClock(Now), new CapturingLogger<NotificationEventHandler>());

        await dispatcher.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.IsNotNull(request);
        Assert.AreEqual(integrationEvent.EventId, request!.MessageId);
        Assert.AreEqual(BookingEventTypes.HoldConfirmationRequested, request.EventType);
        Assert.AreEqual(integrationEvent.Generation, request.Generation);
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
        var dispatcher = new NotificationEventHandler(slots.Object, sender.Object,
            new FakeClock(Now), new CapturingLogger<NotificationEventHandler>());

        await dispatcher.HandleAsync(integrationEvent, CancellationToken.None);

        sender.VerifyNoOtherCalls();
    }

    private static Slot CreateSlot(SlotId id) =>
        new(id, new TestType("BloodTest"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30));

    [DataTestMethod]
    [DataRow(false, false, 1)]
    [DataRow(true, false, 0)]
    [DataRow(false, true, 0)]
    public async Task Handle_OfferActiveStaleOrExpired_SendsOnlyActiveOffer(bool stale, bool expired, int sends)
    {
        var slot = CreateSlot(SlotId.New());
        var patient = PatientId.New();
        Assert.IsTrue(slot.TryOffer(patient, Now, Now.AddMinutes(5)));
        var offer = new OfferCreated(Guid.NewGuid(), slot.Id.Value, patient.Value, slot.TestType.Value,
            stale ? slot.Generation - 1 : slot.Generation, slot.OfferExpiresAtUtc!.Value, Now);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(s => s.GetByIdAsync(slot.Id, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var sender = new Mock<INotificationSender>();
        var handler = new NotificationEventHandler(slots.Object, sender.Object,
            new FakeClock(expired ? Now.AddMinutes(6) : Now), new CapturingLogger<NotificationEventHandler>());

        await handler.HandleAsync(offer, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.Is<NotificationRequest>(n =>
            n.MessageId == offer.EventId && n.EventType == BookingEventTypes.OfferCreated &&
            n.Generation == offer.Generation),
            It.IsAny<CancellationToken>()),
            Times.Exactly(sends));
    }

    [TestMethod]
    public async Task Handle_DuplicateConfirmation_PreservesMessageIdAndAllowsRepeatedSideEffect()
    {
        var confirmed = new AppointmentConfirmed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "BloodTest", Now);
        var sender = new Mock<INotificationSender>();
        var handler = new NotificationEventHandler(Mock.Of<ISlotRepository>(), sender.Object,
            new FakeClock(Now), new CapturingLogger<NotificationEventHandler>());
        await handler.HandleAsync(confirmed, CancellationToken.None);
        await handler.HandleAsync(confirmed, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.Is<NotificationRequest>(n => n.MessageId == confirmed.EventId &&
            n.EventType == BookingEventTypes.AppointmentConfirmed &&
            n.Recipient == confirmed.PatientId.ToString()),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [DataTestMethod]
    [DataRow(false, 7, 1)]
    [DataRow(false, 10, 0)]
    [DataRow(false, -1, 0)]
    [DataRow(true, 7, 1)]
    [DataRow(true, 10, 0)]
    [DataRow(true, -1, 0)]
    public async Task Handle_LeaseExpirationPrecision_SendsOnlyMatchingMicrosecond(bool hold,
        int eventTickOffset, int sends)
    {
        var slot = CreateSlot(SlotId.New());
        var patient = PatientId.New();
        var persistedExpiry = Now.AddMinutes(5);
        Assert.IsTrue(hold
            ? slot.TryHold(patient, Now, persistedExpiry)
            : slot.TryOffer(patient, Now, persistedExpiry));
        var eventExpiry = persistedExpiry.AddTicks(eventTickOffset);
        var eventId = Guid.NewGuid();
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slot.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slot);
        var sender = new Mock<INotificationSender>();
        var handler = new NotificationEventHandler(slots.Object, sender.Object,
            new FakeClock(Now), new CapturingLogger<NotificationEventHandler>());

        if (hold)
        {
            await handler.HandleAsync(new HoldConfirmationRequested(eventId, slot.Id.Value, patient.Value,
                slot.TestType.Value, slot.Generation, eventExpiry, Now), CancellationToken.None);
        }
        else
        {
            await handler.HandleAsync(new OfferCreated(eventId, slot.Id.Value, patient.Value,
                slot.TestType.Value, slot.Generation, eventExpiry, Now), CancellationToken.None);
        }

        sender.Verify(notificationSender => notificationSender.SendAsync(
            It.Is<NotificationRequest>(notification => notification.MessageId == eventId),
            It.IsAny<CancellationToken>()), Times.Exactly(sends));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Handle_PersistedLeaseExpiredBeforeEventTimestamp_DoesNotSendNotification(bool hold)
    {
        var slot = CreateSlot(SlotId.New());
        var patient = PatientId.New();
        var persistedExpiry = Now.AddMinutes(5);
        Assert.IsTrue(hold
            ? slot.TryHold(patient, Now, persistedExpiry)
            : slot.TryOffer(patient, Now, persistedExpiry));
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slot.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slot);
        var sender = new Mock<INotificationSender>(MockBehavior.Strict);
        var handler = new NotificationEventHandler(slots.Object, sender.Object,
            new FakeClock(persistedExpiry.AddTicks(3)), new CapturingLogger<NotificationEventHandler>());
        var eventExpiry = persistedExpiry.AddTicks(7);

        if (hold)
        {
            await handler.HandleAsync(new HoldConfirmationRequested(Guid.NewGuid(), slot.Id.Value,
                patient.Value, slot.TestType.Value, slot.Generation, eventExpiry, Now),
                CancellationToken.None);
        }
        else
        {
            await handler.HandleAsync(new OfferCreated(Guid.NewGuid(), slot.Id.Value, patient.Value,
                slot.TestType.Value, slot.Generation, eventExpiry, Now), CancellationToken.None);
        }

        sender.VerifyNoOtherCalls();
    }

}

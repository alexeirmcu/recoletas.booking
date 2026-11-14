using Moq;
using Recoletas.Booking.ApplicationServices;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class OfferNextWaiterHandlerTests
{
    [TestMethod]
    public async Task Handle_ReleasedSlotAndWaiter_CreatesExclusiveOfferAndRemovesWaiter()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = CreateSlot(slotId, clock.UtcNow);
        var waiter = new WaitlistEntry(Guid.NewGuid(), patientId, slot.TestType, clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        slots.Setup(repository => repository.TryOfferAsync(slotId, patientId, clock.UtcNow,
                clock.UtcNow.AddMinutes(5), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(() =>
            {
                slot.TryOffer(patientId, clock.UtcNow, clock.UtcNow.AddMinutes(5));
                return slot;
            });
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.GetNextAsync(slot.TestType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(waiter);
        waitlist.Setup(repository => repository.TryClaimAsync(waiter.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<OfferCreated>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<bool>();
        var handler = new OfferNextWaiterHandler(slots.Object, waitlist.Object, outbox.Object,
            unitOfWork.Object, clock, new ApplicationServicesOptions());

        var offered = await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);

        Assert.IsTrue(offered);
        Assert.AreEqual(SlotState.Offered, slot.State);
        Assert.AreEqual(patientId, slot.AssignedPatientId);
        waitlist.Verify(repository => repository.TryClaimAsync(waiter.Id, It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(store => store.EnqueueAsync(It.IsAny<OfferCreated>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_OfferFails_RestoresClaimAndDoesNotEnqueueOutbox()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var waiter = new WaitlistEntry(Guid.NewGuid(), PatientId.New(), new TestType("BloodTest"), clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSlot(slotId, clock.UtcNow));
        slots.Setup(repository => repository.TryOfferAsync(slotId, waiter.PatientId, clock.UtcNow,
                clock.UtcNow.AddMinutes(5), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync((Slot?)null);
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.GetNextAsync(waiter.TestType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(waiter);
        waitlist.Setup(repository => repository.TryClaimAsync(waiter.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        waitlist.Setup(repository => repository.TryRestoreClaimAsync(waiter.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<bool>();
        var handler = new OfferNextWaiterHandler(slots.Object, waitlist.Object, outbox.Object,
            unitOfWork.Object, clock, new ApplicationServicesOptions());

        var offered = await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);

        Assert.IsFalse(offered);
        waitlist.Verify(repository => repository.TryClaimAsync(waiter.Id, It.IsAny<CancellationToken>()), Times.Once);
        waitlist.Verify(repository => repository.TryRestoreClaimAsync(waiter.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        outbox.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Handle_EmptyWaitlist_ReturnsFalseWithoutOffer()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var slot = CreateSlot(slotId, clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.GetNextAsync(slot.TestType, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WaitlistEntry?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<bool>();
        var handler = new OfferNextWaiterHandler(slots.Object, waitlist.Object, Mock.Of<IOutboxStore>(),
            unitOfWork.Object, clock, new ApplicationServicesOptions());

        var offered = await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);

        Assert.IsFalse(offered);
        Assert.AreEqual(SlotState.Available, slot.State);
    }

    [TestMethod]
    public async Task Handle_NonPositiveOfferWindow_ReturnsFalseWithoutRepositoryCalls()
    {
        var slotId = SlotId.New();
        var slots = new Mock<ISlotRepository>();
        var waitlist = new Mock<IWaitlistRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var options = new ApplicationServicesOptions { OfferWindow = TimeSpan.Zero };
        var handler = new OfferNextWaiterHandler(slots.Object, waitlist.Object, Mock.Of<IOutboxStore>(),
            unitOfWork.Object, new FakeClock(DateTimeOffset.UtcNow), options);

        var offered = await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);

        Assert.IsFalse(offered);
        slots.VerifyNoOtherCalls();
        waitlist.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    private static Slot CreateSlot(SlotId id, DateTimeOffset now) =>
        new(id, new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
}

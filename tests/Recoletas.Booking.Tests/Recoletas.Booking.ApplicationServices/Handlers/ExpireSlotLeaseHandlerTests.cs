using Moq;
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
public sealed class ExpireSlotLeaseHandlerTests
{
    [TestMethod]
    public async Task Handle_ExpiredLease_ReleasesSlotAndEnqueuesEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var slot = new Slot(slotId, new TestType("BloodTest"), clock.UtcNow.AddDays(1),
            clock.UtcNow.AddDays(1).AddMinutes(30));
        var patientId = PatientId.New();
        slot.TryHold(patientId, clock.UtcNow.AddMinutes(-10), clock.UtcNow.AddMinutes(-5));
        slot.ExpireLease(clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryExpireLeaseAsync(slotId, clock.UtcNow,
                It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<SlotReleased>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<bool>();
        var handler = new ExpireSlotLeaseHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var expired = await handler.Handle(new ExpireSlotLease(slotId.Value), CancellationToken.None);

        Assert.IsTrue(expired);
        Assert.AreEqual(SlotState.Available, slot.State);
        outbox.Verify(store => store.EnqueueAsync(It.IsAny<SlotReleased>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_NoExpiredLease_ReturnsFalseWithoutOutboxWrite()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryExpireLeaseAsync(It.IsAny<SlotId>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>())).ReturnsAsync((Slot?)null);
        var outbox = new Mock<IOutboxStore>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<bool>();
        var handler = new ExpireSlotLeaseHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var slotId = SlotId.New();
        var expired = await handler.Handle(new ExpireSlotLease(slotId.Value), CancellationToken.None);

        Assert.IsFalse(expired);
        outbox.VerifyNoOtherCalls();
    }
}

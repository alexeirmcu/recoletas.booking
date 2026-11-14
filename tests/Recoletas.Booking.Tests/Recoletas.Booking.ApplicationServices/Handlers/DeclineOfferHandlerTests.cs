using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class DeclineOfferHandlerTests
{
    [TestMethod]
    public async Task Handle_CurrentOffer_ReleasesSlotAndEnqueuesReleaseEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = new Slot(slotId, new TestType("BloodTest"), clock.UtcNow.AddDays(1),
            clock.UtcNow.AddDays(1).AddMinutes(30));
        slot.TryOffer(patientId, clock.UtcNow, clock.UtcNow.AddMinutes(5));
        slot.TryDeclineOffer(patientId, slot.Generation, clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryDeclineOfferAsync(slotId, patientId, 1, clock.UtcNow,
                It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<SlotReleased>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new DeclineOfferHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new DeclineOffer(slotId.Value, patientId.Value,
            new DeclineOfferRequest(1)), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(SlotState.Available, slot.State);
        outbox.Verify(store => store.EnqueueAsync(It.IsAny<SlotReleased>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_StaleOffer_ReturnsConflictWithoutOutboxEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryDeclineOfferAsync(It.IsAny<SlotId>(), It.IsAny<PatientId>(),
                It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Slot?)null);
        var outbox = new Mock<IOutboxStore>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new DeclineOfferHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new DeclineOffer(Guid.NewGuid(), Guid.NewGuid(),
            new DeclineOfferRequest(9)), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.OfferInvalidOrExpired, result.ErrorCode);
        outbox.VerifyNoOtherCalls();
    }
}

using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class AcceptOfferHandlerTests
{
    [TestMethod]
    public async Task Handle_CurrentOffer_ConfirmsAppointmentAndRemovesWaitlistEntry()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = new Slot(slotId, new TestType("BloodTest"), clock.UtcNow.AddDays(1),
            clock.UtcNow.AddDays(1).AddMinutes(30));
        slot.TryOffer(patientId, clock.UtcNow, clock.UtcNow.AddMinutes(5));
        var generation = slot.Generation;
        var reservation = slot.TryAcceptOffer(patientId, generation, clock.UtcNow)!;
        var mutation = new SlotWrapper<Reservation>(slot, reservation);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        slots.Setup(repository => repository.TryAcceptOfferAsync(slotId, patientId, generation, clock.UtcNow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mutation);
        var entry = new WaitlistEntry(Guid.NewGuid(), patientId, slot.TestType, clock.UtcNow);
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.GetActiveEntryAsync(patientId, slot.TestType,
                It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        waitlist.Setup(repository => repository.RemoveAsync(entry.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<AppointmentConfirmed>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new AcceptOfferHandler(slots.Object, waitlist.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new AcceptOffer(slotId.Value, patientId.Value,
            new AcceptOfferRequest(generation)),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(reservation.Id, result.Appointment?.ReservationId);
        waitlist.Verify(repository => repository.RemoveAsync(entry.Id, It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(store => store.EnqueueAsync(It.Is<AppointmentConfirmed>(item =>
            item.ReservationId == reservation.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_MissingSlot_ReturnsNotFound()
    {
        var slotId = SlotId.New();
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Slot?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new AcceptOfferHandler(slots.Object, Mock.Of<IWaitlistRepository>(), Mock.Of<IOutboxStore>(),
            unitOfWork.Object, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.Handle(new AcceptOffer(slotId.Value, Guid.NewGuid(),
            new AcceptOfferRequest(1)), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.SlotNotFound, result.ErrorCode);
    }

    [TestMethod]
    public async Task Handle_StaleOrExpiredOffer_ReturnsConflict()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = new Slot(slotId, new TestType("BloodTest"), clock.UtcNow.AddDays(1),
            clock.UtcNow.AddDays(1).AddMinutes(30));
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        slots.Setup(repository => repository.TryAcceptOfferAsync(slotId, patientId, 7, It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SlotWrapper<Reservation>?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new AcceptOfferHandler(slots.Object, Mock.Of<IWaitlistRepository>(), Mock.Of<IOutboxStore>(),
            unitOfWork.Object, clock);

        var result = await handler.Handle(new AcceptOffer(slotId.Value, patientId.Value,
            new AcceptOfferRequest(7)), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.OfferInvalidOrExpired, result.ErrorCode);
    }
}

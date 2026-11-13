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
public sealed class ConfirmHoldHandlerTests
{
    [TestMethod]
    public async Task Handle_ValidHold_ConfirmsAppointmentAndEnqueuesEvent()
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = new Slot(slotId, new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
        slot.TryHold(patientId, now, now.AddMinutes(5));
        var reservation = slot.TryConfirmHold(patientId, now)!;
        var mutation = new SlotWrapper<Reservation>(slot, reservation);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryConfirmAsync(slotId, patientId, 1, now,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mutation);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<AppointmentConfirmed>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new ConfirmHoldHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new ConfirmHold(slotId.Value, patientId.Value,
            new ConfirmHoldRequest(1)), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(reservation.Id, result.Appointment?.ReservationId);
        outbox.Verify(store => store.EnqueueAsync(It.Is<AppointmentConfirmed>(item =>
            item.ReservationId == reservation.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_ExpiredOrStaleHold_ReturnsConflictWithoutOutboxEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryConfirmAsync(slotId, patientId, 3, It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SlotWrapper<Reservation>?)null);
        var outbox = new Mock<IOutboxStore>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new ConfirmHoldHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new ConfirmHold(slotId.Value, patientId.Value,
            new ConfirmHoldRequest(3)), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.HoldInvalidOrExpired, result.ErrorCode);
        outbox.VerifyNoOtherCalls();
    }
}

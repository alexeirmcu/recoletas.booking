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
public sealed class CancelAppointmentHandlerTests
{
    [TestMethod]
    public async Task Handle_ExistingReservation_ReleasesSlotAndEnqueuesReleaseEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var patientId = PatientId.New();
        var slot = new Slot(SlotId.New(), new TestType("BloodTest"), clock.UtcNow.AddDays(1),
            clock.UtcNow.AddDays(1).AddMinutes(30));
        slot.TryHold(patientId, clock.UtcNow, clock.UtcNow.AddMinutes(5));
        var reservation = slot.TryConfirmHold(patientId, clock.UtcNow)!;
        slot.TryCancel(patientId, reservation.Id, clock.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryCancelAsync(reservation.Id, patientId, clock.UtcNow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(slot);
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<SlotReleased>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new CancelAppointmentHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new CancelAppointment(reservation.Id, patientId.Value), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        outbox.Verify(store => store.EnqueueAsync(It.Is<SlotReleased>(item => item.SlotId == slot.Id.Value),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_UnknownReservation_ReturnsNotFoundWithoutOutboxEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.TryCancelAsync(It.IsAny<Guid>(), It.IsAny<PatientId>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Slot?)null);
        var outbox = new Mock<IOutboxStore>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new CancelAppointmentHandler(slots.Object, outbox.Object, unitOfWork.Object, clock);

        var result = await handler.Handle(new CancelAppointment(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.AppointmentNotFound, result.ErrorCode);
        outbox.VerifyNoOtherCalls();
    }
}

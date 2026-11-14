using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class QueryAppointmentsHandlerTests
{
    [TestMethod]
    public async Task Handle_PatientReservations_ReturnsOnlyCurrentConfirmedReservation()
    {
        var patientId = PatientId.New();
        var now = DateTimeOffset.UtcNow;
        var validSlot = new Slot(SlotId.New(), new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
        validSlot.TryHold(patientId, now, now.AddMinutes(5));
        var validReservation = validSlot.TryConfirmHold(patientId, now)!;
        var obsoleteSlot = new Slot(SlotId.New(), new TestType("Ultrasound"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
        var obsoleteReservation = Reservation.Create(obsoleteSlot.Id, patientId, now);
        var appointments = new Mock<IAppointmentRepository>();
        appointments.Setup(repository => repository.ListForPatientAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { validReservation, obsoleteReservation });
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(validSlot.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validSlot);
        slots.Setup(repository => repository.GetByIdAsync(obsoleteSlot.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(obsoleteSlot);
        var handler = new QueryAppointmentsHandler(appointments.Object, slots.Object);

        var result = await handler.Handle(new QueryAppointments(patientId.Value), CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(validReservation.Id, result[0].ReservationId);
        Assert.AreEqual(validSlot.StartsAtUtc, result[0].StartsAtUtc);
        Assert.AreEqual(validSlot.EndsAtUtc, result[0].EndsAtUtc);
        Assert.AreEqual(validReservation.ConfirmedAtUtc, result[0].ConfirmedAtUtc);
    }
}

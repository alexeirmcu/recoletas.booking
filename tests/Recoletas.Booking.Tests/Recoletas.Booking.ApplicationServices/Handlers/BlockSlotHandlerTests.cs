using Moq;
using Recoletas.Booking.ApplicationServices;
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
public sealed class BlockSlotHandlerTests
{
    [TestMethod]
    public async Task Handle_AvailableSlot_ReturnsGenerationAndSetsFiveMinuteHold()
    {
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = CreateSlot(slotId, clock.UtcNow);
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patient(patientId, "Test Patient", clock.UtcNow));
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        slots.Setup(repository => repository.TryHoldAsync(slotId, patientId, clock.UtcNow,
                clock.UtcNow.AddMinutes(5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                slot.TryHold(patientId, clock.UtcNow, clock.UtcNow.AddMinutes(5));
                return slot;
            });
        var outbox = new Mock<IOutboxStore>();
        HoldConfirmationRequested? enqueuedEvent = null;
        outbox.Setup(store => store.EnqueueAsync(It.IsAny<HoldConfirmationRequested>(), It.IsAny<CancellationToken>()))
            .Callback<HoldConfirmationRequested, CancellationToken>((integrationEvent, _) =>
                enqueuedEvent = integrationEvent)
            .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new BlockSlotHandler(slots.Object, patients.Object, outbox.Object, unitOfWork.Object, clock,
            new ApplicationServicesOptions());

        var result = await handler.Handle(new BlockSlot(slotId.Value, patientId.Value), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Generation);
        Assert.AreEqual(clock.UtcNow.AddMinutes(5), slot.HoldExpiresAtUtc);
        Assert.IsNotNull(enqueuedEvent);
        Assert.AreEqual(slotId.Value, enqueuedEvent!.SlotId);
        Assert.AreEqual(patientId.Value, enqueuedEvent.PatientId);
        Assert.AreEqual("BloodTest", enqueuedEvent.TestType);
        Assert.AreEqual(1, enqueuedEvent.Generation);
        Assert.AreEqual(clock.UtcNow.AddMinutes(5), enqueuedEvent.ExpiresAtUtc);
        Assert.AreEqual(clock.UtcNow, enqueuedEvent.OccurredAtUtc);
        Assert.AreNotEqual(Guid.Empty, enqueuedEvent.EventId);
    }

    [TestMethod]
    public async Task Handle_UnknownPatient_ReturnsNotFoundWithoutHoldingSlot()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var patientId = PatientId.New();
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Patient?)null);
        var slots = new Mock<ISlotRepository>(MockBehavior.Strict);
        var handler = new BlockSlotHandler(slots.Object, patients.Object, Mock.Of<IOutboxStore>(),
            Mock.Of<IUnitOfWork>(), clock, new ApplicationServicesOptions());

        var result = await handler.Handle(new BlockSlot(SlotId.New().Value, patientId.Value), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.PatientNotFound, result.ErrorCode);
        slots.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Handle_SlotAlreadyTaken_ReturnsConflictResult()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patient(patientId, "Test Patient", clock.UtcNow));
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.GetByIdAsync(slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSlot(slotId, clock.UtcNow));
        slots.Setup(repository => repository.TryHoldAsync(slotId, patientId, It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Slot?)null);
        var outbox = new Mock<IOutboxStore>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.ExecuteInline<BookingResult>();
        var handler = new BlockSlotHandler(slots.Object, patients.Object, outbox.Object,
            unitOfWork.Object, clock, new ApplicationServicesOptions());

        var result = await handler.Handle(new BlockSlot(slotId.Value, patientId.Value), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(BookingErrorCodes.SlotUnavailable, result.ErrorCode);
        outbox.VerifyNoOtherCalls();
    }

    private static Slot CreateSlot(SlotId id, DateTimeOffset now) =>
        new(id, new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
}

using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class JoinWaitlistHandlerTests
{
    [TestMethod]
    public async Task Handle_NewWaiter_AddsEntryInUtcAndReturnsJoined()
    {
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 1, 15, 0, 0, TimeSpan.FromHours(3)));
        var patientId = PatientId.New();
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patient(patientId, "Test Patient", clock.UtcNow));
        WaitlistEntry? added = null;
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.TryAddAsync(It.IsAny<WaitlistEntry>(), It.IsAny<CancellationToken>()))
            .Callback<WaitlistEntry, CancellationToken>((entry, _) => added = entry).ReturnsAsync(true);
        var handler = new JoinWaitlistHandler(waitlist.Object, patients.Object, clock);

        var result = await handler.Handle(new JoinWaitlist(patientId.Value,
            new JoinWaitlistRequest("BloodTest")), CancellationToken.None);

        Assert.IsTrue(result.Joined);
        Assert.IsFalse(result.AlreadyJoined);
        Assert.IsNotNull(added);
        Assert.AreEqual(TimeSpan.Zero, added.JoinedAtUtc.Offset);
        Assert.AreEqual("BloodTest", added.TestType.Value);
    }

    [TestMethod]
    public async Task Handle_AlreadyJoined_ReturnsDuplicateResult()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var patientId = PatientId.New();
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patient(patientId, "Test Patient", clock.UtcNow));
        var waitlist = new Mock<IWaitlistRepository>();
        waitlist.Setup(repository => repository.TryAddAsync(It.IsAny<WaitlistEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var handler = new JoinWaitlistHandler(waitlist.Object, patients.Object, clock);

        var result = await handler.Handle(new JoinWaitlist(patientId.Value,
            new JoinWaitlistRequest("BloodTest")), CancellationToken.None);

        Assert.IsFalse(result.Joined);
        Assert.IsTrue(result.AlreadyJoined);
        Assert.AreEqual(BookingErrorCodes.AlreadyJoined, result.ErrorCode);
    }

    [TestMethod]
    public async Task Handle_UnknownPatient_ReturnsNotFoundWithoutAddingEntry()
    {
        var patientId = PatientId.New();
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Patient?)null);
        var waitlist = new Mock<IWaitlistRepository>(MockBehavior.Strict);
        var handler = new JoinWaitlistHandler(waitlist.Object, patients.Object,
            new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.Handle(new JoinWaitlist(patientId.Value,
            new JoinWaitlistRequest("BloodTest")), CancellationToken.None);

        Assert.IsFalse(result.Joined);
        Assert.AreEqual(BookingErrorCodes.PatientNotFound, result.ErrorCode);
        waitlist.VerifyNoOtherCalls();
    }
}

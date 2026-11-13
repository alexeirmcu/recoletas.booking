using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Exceptions;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class RegisterPatientHandlerTests
{
    [TestMethod]
    public async Task Handle_NewPatient_PersistsPatientWithClockTimestamp()
    {
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 1, 15, 0, 0, TimeSpan.FromHours(3)));
        Patient? added = null;
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.AddAsync(It.IsAny<Patient>(), It.IsAny<CancellationToken>()))
            .Callback<Patient, CancellationToken>((patient, _) => added = patient)
            .Returns(Task.CompletedTask);
        var handler = new RegisterPatientHandler(patients.Object, clock);

        var result = await handler.Handle(new RegisterPatient(
            new RegisterPatientRequest("Test Patient", "NHC-123")), CancellationToken.None);

        Assert.IsNotNull(added);
        Assert.AreEqual(added.Id.Value, result.PatientId);
        Assert.AreEqual("Test Patient", result.Name);
        Assert.AreEqual("NHC-123", result.Nhc);
        Assert.AreEqual(clock.UtcNow, result.CreatedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, result.CreatedAtUtc.Offset);
    }

    [TestMethod]
    public async Task Handle_DuplicateNhc_ThrowsApplicationConflictException()
    {
        var patients = new Mock<IPatientRepository>();
        patients.Setup(repository => repository.AddAsync(It.IsAny<Patient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicatePatientNhcException());
        var handler = new RegisterPatientHandler(patients.Object, new FakeClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsExceptionAsync<PatientNhcAlreadyExistsException>(() => handler.Handle(
            new RegisterPatient(new RegisterPatientRequest("Test Patient", "NHC-123")), CancellationToken.None));
    }
}

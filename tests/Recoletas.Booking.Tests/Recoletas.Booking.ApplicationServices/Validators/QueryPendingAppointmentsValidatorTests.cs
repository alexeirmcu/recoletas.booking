using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Validators;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Validators;

[TestClass]
[TestCategory("Unit")]
public sealed class QueryPendingAppointmentsValidatorTests
{
    [TestMethod]
    public void Validate_EmptyPatientId_IsInvalid()
    {
        var validator = new QueryPendingAppointmentsValidator();

        var result = validator.Validate(new QueryPendingAppointments(Guid.Empty));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(nameof(QueryPendingAppointments.PatientId), result.Errors[0].PropertyName);
    }

    [TestMethod]
    public void Validate_NonemptyPatientId_IsValid()
    {
        var validator = new QueryPendingAppointmentsValidator();

        var result = validator.Validate(new QueryPendingAppointments(Guid.NewGuid()));

        Assert.IsTrue(result.IsValid);
    }
}

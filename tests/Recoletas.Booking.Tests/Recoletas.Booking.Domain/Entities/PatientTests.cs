using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Domain.Entities;

[TestClass]
[TestCategory("Unit")]
public sealed class PatientTests
{
    [TestMethod]
    public void Constructor_WhenTimestampHasOffset_NormalizesCreatedAtToUtc()
    {
        var created = new DateTimeOffset(2030, 1, 1, 15, 0, 0, TimeSpan.FromHours(3));

        var patient = new Patient(PatientId.New(), "Test Patient", created);

        Assert.AreEqual(TimeSpan.Zero, patient.CreatedAtUtc.Offset);
        Assert.AreEqual(created.UtcDateTime, patient.CreatedAtUtc.UtcDateTime);
    }

    [TestMethod]
    public void Constructor_WhenNhcHasWhitespace_TrimsValue()
    {
        var patient = new Patient(PatientId.New(), "Test Patient", DateTimeOffset.UtcNow, "  NHC-123 \t");

        Assert.AreEqual("NHC-123", patient.Nhc);
    }

    [TestMethod]
    public void Constructor_WhenNhcIsWhitespaceOnly_StoresNull()
    {
        var patient = new Patient(PatientId.New(), "Test Patient", DateTimeOffset.UtcNow, " \t\r\n ");

        Assert.IsNull(patient.Nhc);
    }
}

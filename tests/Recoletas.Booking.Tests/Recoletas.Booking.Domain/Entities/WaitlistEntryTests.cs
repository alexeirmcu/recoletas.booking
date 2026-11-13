using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Domain.Entities;

[TestClass]
[TestCategory("Unit")]
public sealed class WaitlistEntryTests
{
    [TestMethod]
    public void Remove_WhenEntryIsActive_MarksEntryInactive()
    {
        var entry = new WaitlistEntry(Guid.NewGuid(), PatientId.New(), new TestType("BloodTest"),
            new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

        entry.Remove();

        Assert.IsFalse(entry.IsActive);
    }

    [TestMethod]
    public void Constructor_WhenIdentifierIsEmpty_ThrowsArgumentException()
    {
        try
        {
            _ = new WaitlistEntry(Guid.Empty, PatientId.New(), new TestType("BloodTest"), DateTimeOffset.UtcNow);
            Assert.Fail("Expected an empty identifier to be rejected.");
        }
        catch (ArgumentException exception)
        {
            Assert.AreEqual("id", exception.ParamName);
        }
    }
}

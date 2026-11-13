using Microsoft.EntityFrameworkCore;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class BookingDatabaseInitializerTests
{
    private static readonly DateTimeOffset Now = new(2040, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InitializeAsync_DevelopmentSeedingCalledTwice_CreatesOneOfEachDemoSlot()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var tomorrow = new DateTimeOffset(Now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        var expectedSlots = new[]
        {
            (new TestType("BloodTest"), tomorrow.AddHours(9)),
            (new TestType("BloodTest"), tomorrow.AddHours(10)),
            (new TestType("Ultrasound"), tomorrow.AddHours(11))
        };

        try
        {
            await using (var firstContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
            {
                await new BookingDatabaseInitializer(firstContext, new FakeClock(Now)).InitializeAsync(true);
            }

            await using (var secondContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
            {
                await new BookingDatabaseInitializer(secondContext, new FakeClock(Now)).InitializeAsync(true);
            }

            await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
            var persistedSlots = await verifyContext.Slots.AsNoTracking().ToListAsync();

            foreach (var (testType, startsAtUtc) in expectedSlots)
            {
                var count = persistedSlots.Count(slot =>
                    slot.TestType == testType &&
                    slot.StartsAtUtc == startsAtUtc &&
                    slot.EndsAtUtc == startsAtUtc.AddMinutes(30));
                Assert.AreEqual(1, count, $"Expected one {testType} slot starting at {startsAtUtc:O}.");
            }
        }
        finally
        {
            await using var cleanupContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
            await cleanupContext.Slots
                .Where(slot => slot.StartsAtUtc >= tomorrow && slot.StartsAtUtc < tomorrow.AddDays(1))
                .ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task InitializeAsync_DemoSeedingDisabledCalledTwice_KeepsExactlyOneOfEachTestType()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();

        await using (var firstContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            await new BookingDatabaseInitializer(firstContext, new FakeClock(Now)).InitializeAsync(false);
        }

        await using (var secondContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            await new BookingDatabaseInitializer(secondContext, new FakeClock(Now)).InitializeAsync(false);
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var catalogCodes = (await verifyContext.TestTypes.AsNoTracking().ToListAsync())
            .Select(testType => testType.Code.Value)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "BloodTest", "MRI", "Ultrasound" }, catalogCodes);
    }
}

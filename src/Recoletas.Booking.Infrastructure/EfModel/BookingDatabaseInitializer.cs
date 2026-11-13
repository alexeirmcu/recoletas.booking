using Microsoft.EntityFrameworkCore;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Services;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class BookingDatabaseInitializer(BookingDbContext dbContext, IClock clock)
{
    private static readonly (string TestType, int StartHour)[] DemoSlotDefinitions =
    [
        ("BloodTest", 9),
        ("BloodTest", 10),
        ("Ultrasound", 11)
    ];

    public async Task InitializeAsync(bool seedDemoData, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        await SeedTestTypesAsync(cancellationToken);

        if (!seedDemoData)
        {
            return;
        }

        var tomorrowUtc = clock.UtcNow.ToUniversalTime().UtcDateTime.Date.AddDays(1);
        foreach (var (testTypeName, startHour) in DemoSlotDefinitions)
        {
            var startsAtUtc = new DateTimeOffset(tomorrowUtc.AddHours(startHour), TimeSpan.Zero);
            var endsAtUtc = startsAtUtc.AddMinutes(30);
            var testType = new TestType(testTypeName);
            var exists = await dbContext.Slots.AnyAsync(slot =>
                slot.TestType == testType &&
                slot.StartsAtUtc == startsAtUtc &&
                slot.EndsAtUtc == endsAtUtc,
                cancellationToken);

            if (!exists)
            {
                dbContext.Slots.Add(new Slot(SlotId.New(), testType, startsAtUtc, endsAtUtc));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedTestTypesAsync(CancellationToken cancellationToken)
    {
        foreach (var testType in TestType.SupportedTypes)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"TestTypes\" (\"Code\") VALUES ({testType.Value}) ON CONFLICT (\"Code\") DO NOTHING",
                cancellationToken);
        }
    }
}

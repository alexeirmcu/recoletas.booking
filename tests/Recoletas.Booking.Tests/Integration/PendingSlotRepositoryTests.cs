using Microsoft.EntityFrameworkCore;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Repositories;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class PendingSlotRepositoryTests
{
    [TestMethod]
    public async Task ListPendingForPatientAsync_ActiveAssignments_ReturnsEligibleRowsInStableExpiryOrderWithoutMutation()
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("pendingread");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        var now = new DateTimeOffset(2030, 5, 6, 12, 0, 0, TimeSpan.Zero);
        var patientId = PatientId.New();
        var otherPatientId = PatientId.New();
        var patient = new Patient(patientId, "Pending patient", now);
        var otherPatient = new Patient(otherPatientId, "Other patient", now);
        var inProgressId = SlotId.New();
        var tiedFirstId = new SlotId(Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var tiedSecondId = new SlotId(Guid.Parse("00000000-0000-0000-0000-000000000102"));
        var latestId = SlotId.New();
        var eligible = new[]
        {
            CreatePendingSlot(inProgressId, patientId, now.AddMinutes(-5), now.AddMinutes(15),
                SlotState.Held, now.AddMinutes(5), now.AddMinutes(-1)),
            CreatePendingSlot(tiedSecondId, patientId, now.AddHours(2), now.AddHours(2).AddMinutes(30),
                SlotState.Held, now.AddMinutes(10), now.AddMinutes(-1)),
            CreatePendingSlot(tiedFirstId, patientId, now.AddHours(2), now.AddHours(2).AddMinutes(30),
                SlotState.Offered, now.AddMinutes(10), now.AddMinutes(-1)),
            CreatePendingSlot(latestId, patientId, now.AddHours(3), now.AddHours(3).AddMinutes(30),
                SlotState.Offered, now.AddMinutes(20), now.AddMinutes(-1))
        };
        var excluded = new[]
        {
            CreatePendingSlot(SlotId.New(), otherPatientId, now.AddHours(1), now.AddHours(2),
                SlotState.Held, now.AddMinutes(5), now.AddMinutes(-1)),
            CreatePendingSlot(SlotId.New(), patientId, now.AddHours(1), now.AddHours(2),
                SlotState.Held, now.AddMinutes(-1), now.AddMinutes(-2)),
            CreatePendingSlot(SlotId.New(), patientId, now.AddHours(1), now.AddHours(2),
                SlotState.Offered, now, now.AddMinutes(-1)),
            CreateConfirmedSlot(patientId, now),
            new Slot(SlotId.New(), new TestType("BloodTest"), now.AddHours(1), now.AddHours(2)),
            CreatePendingSlot(SlotId.New(), patientId, now.AddHours(-1), now,
                SlotState.Held, now.AddHours(2), now.AddMinutes(-1)),
            CreatePendingSlot(SlotId.New(), patientId, now.AddHours(-1), now.AddMinutes(-1),
                SlotState.Offered, now.AddHours(2), now.AddMinutes(-2))
        };
        var waitlistEntry = new WaitlistEntry(Guid.NewGuid(), patientId, new TestType("BloodTest"), now);
        var outboxRecord = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EventType = "test_event",
            Payload = "{}",
            OccurredAtUtc = now,
            AvailableAtUtc = now
        };

        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString)))
        {
            seedContext.Patients.AddRange(patient, otherPatient);
            seedContext.Slots.AddRange(eligible);
            seedContext.Slots.AddRange(excluded);
            seedContext.WaitlistEntries.Add(waitlistEntry);
            seedContext.OutboxMessages.Add(outboxRecord);
            await seedContext.SaveChangesAsync();
        }

        IReadOnlyList<Slot> results;
        await using (var queryContext = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString)))
        {
            results = await new SlotRepository(queryContext)
                .ListPendingForPatientAsync(patientId, now, CancellationToken.None);
        }

        Assert.AreEqual(4, results.Count);
        CollectionAssert.AreEqual(
            new[] { inProgressId.Value, tiedFirstId.Value, tiedSecondId.Value, latestId.Value },
            results.Select(slot => slot.Id.Value).ToArray());
        Assert.AreEqual(SlotState.Held, results[0].State);
        Assert.AreEqual(now.AddMinutes(5), results[0].HoldExpiresAtUtc);

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString));
        foreach (var original in eligible.Concat(excluded))
        {
            var persisted = await verifyContext.Slots.AsNoTracking()
                .SingleAsync(slot => slot.Id == original.Id);
            Assert.AreEqual(original.State, persisted.State);
            Assert.AreEqual(original.Generation, persisted.Generation);
            Assert.AreEqual(original.AssignedPatientId, persisted.AssignedPatientId);
            Assert.AreEqual(original.HoldExpiresAtUtc, persisted.HoldExpiresAtUtc);
            Assert.AreEqual(original.OfferExpiresAtUtc, persisted.OfferExpiresAtUtc);
        }

        Assert.AreEqual(1, await verifyContext.WaitlistEntries.CountAsync());
        Assert.IsTrue((await verifyContext.WaitlistEntries.AsNoTracking().SingleAsync()).IsActive);
        Assert.AreEqual(1, await verifyContext.OutboxMessages.CountAsync());
        Assert.AreEqual(outboxRecord.Id,
            (await verifyContext.OutboxMessages.AsNoTracking().SingleAsync()).Id);
    }

    private static Slot CreatePendingSlot(SlotId id, PatientId patientId, DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc, SlotState state, DateTimeOffset expiresAtUtc, DateTimeOffset transitionAtUtc)
    {
        var slot = new Slot(id, new TestType("BloodTest"), startsAtUtc, endsAtUtc);
        var succeeded = state == SlotState.Held
            ? slot.TryHold(patientId, transitionAtUtc, expiresAtUtc)
            : slot.TryOffer(patientId, transitionAtUtc, expiresAtUtc);
        Assert.IsTrue(succeeded);
        return slot;
    }

    private static Slot CreateConfirmedSlot(PatientId patientId, DateTimeOffset now)
    {
        var slot = new Slot(SlotId.New(), new TestType("BloodTest"), now.AddHours(1), now.AddHours(2));
        Assert.IsTrue(slot.TryHold(patientId, now.AddMinutes(-1), now.AddMinutes(5)));
        Assert.IsNotNull(slot.TryConfirmHold(patientId, now));
        return slot;
    }
}

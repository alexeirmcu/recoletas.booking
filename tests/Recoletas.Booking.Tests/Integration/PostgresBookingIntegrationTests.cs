using Microsoft.EntityFrameworkCore;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.ApplicationServices;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Outbox;
using Recoletas.Booking.Infrastructure.Repositories;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresBookingIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PatientId FifoFirstPatientId =
        new(Guid.Parse("84674d8f-3fcb-4e11-9f01-21a9f7cbfa01"));
    private static readonly PatientId FifoSecondPatientId =
        new(Guid.Parse("84674d8f-3fcb-4e11-9f01-21a9f7cbfa02"));

    [TestMethod]
    public async Task TryHoldAsync_ConcurrentClaimsOnSameSlot_ExactlyOnePatientWins()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var patientIds = new[] { PatientId.New(), PatientId.New() };
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(CreateSlot(slotId));
            seedContext.Patients.AddRange(patientIds.Select(CreatePatient));
            await seedContext.SaveChangesAsync();
        }

        var attempts = patientIds.Select(async patientId =>
        {
            await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
            var repository = new SlotRepository(context);
            return await repository.TryHoldAsync(slotId, patientId, Now, Now.AddMinutes(5), CancellationToken.None);
        });

        var results = await Task.WhenAll(attempts);

        Assert.AreEqual(1, results.Count(result => result is not null));
        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(slot => slot.Id == slotId);
        Assert.AreEqual(SlotState.Held, persisted.State);
        Assert.IsTrue(persisted.AssignedPatientId.HasValue);
        Assert.IsTrue(patientIds.Contains(persisted.AssignedPatientId.Value));
    }

    [TestMethod]
    public async Task BlockSlotHandler_Commit_PersistsHoldAndConfirmationRequestTogether()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(CreateSlot(slotId));
            seedContext.Patients.Add(CreatePatient(patientId));
            await seedContext.SaveChangesAsync();
        }

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now);
            var handler = new BlockSlotHandler(new SlotRepository(context), new PatientRepository(context),
                new OutboxStore(context, clock), new UnitOfWork(context), clock, new ApplicationServicesOptions());
            var result = await handler.Handle(new BlockSlot(slotId.Value, patientId.Value), CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(1, result.Generation);
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == slotId);
        var eventRecord = await verifyContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.EventType == "HoldConfirmationRequested" &&
                                             message.Payload.Contains(slotId.Value.ToString()));
        Assert.AreEqual(SlotState.Held, persisted.State);
        Assert.AreEqual(patientId, persisted.AssignedPatientId);
        Assert.IsNotNull(eventRecord);
    }

    [TestMethod]
    public async Task UnitOfWork_WhenHoldAndOutboxWriteFail_RollsBackBoth()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(CreateSlot(slotId));
            seedContext.Patients.Add(CreatePatient(patientId));
            await seedContext.SaveChangesAsync();
        }

        var outboxEventId = Guid.Empty;
        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now);
            var repository = new SlotRepository(context);
            var outbox = new OutboxStore(context, clock);
            var unitOfWork = new UnitOfWork(context);
            try
            {
                await unitOfWork.ExecuteAsync<int>(async cancellationToken =>
                {
                    var held = await repository.TryHoldAsync(slotId, patientId, clock.UtcNow,
                        clock.UtcNow.AddMinutes(5), cancellationToken);
                    Assert.IsNotNull(held);
                    var holdEvent = (HoldConfirmationRequested)held!.DrainDomainEvents().Single();
                    outboxEventId = holdEvent.EventId;
                    await outbox.EnqueueAsync(holdEvent, cancellationToken);
                    throw new InvalidOperationException("Force hold transaction rollback.");
                }, CancellationToken.None);
                Assert.Fail("The transaction operation should throw.");
            }
            catch (InvalidOperationException exception) when (exception.Message == "Force hold transaction rollback.")
            {
                // The slot transition and its outbox message share this transaction.
            }
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == slotId);
        var outboxRecord = await verifyContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == outboxEventId);
        Assert.AreEqual(SlotState.Available, persisted.State);
        Assert.IsNull(outboxRecord);
    }

    [TestMethod]
    public async Task ConfirmHoldHandler_Commit_PersistsSlotAndOutboxEventTogether()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = CreateSlot(slotId);
        slot.TryHold(patientId, Now, Now.AddMinutes(5));
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(slot);
            seedContext.Patients.Add(CreatePatient(patientId));
            await seedContext.SaveChangesAsync();
        }

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now.AddMinutes(1));
            var handler = new ConfirmHoldHandler(new SlotRepository(context), new OutboxStore(context, clock),
                new UnitOfWork(context), clock);
            var result = await handler.Handle(new ConfirmHold(slotId.Value, patientId.Value,
                new ConfirmHoldRequest(1)), CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == slotId);
        var eventRecord = await verifyContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.EventType == "AppointmentConfirmed" &&
                                             message.Payload.Contains(slotId.Value.ToString()));
        Assert.AreEqual(SlotState.Confirmed, persisted.State);
        Assert.IsNotNull(eventRecord);
    }

    [TestMethod]
    public async Task UnitOfWork_WhenOperationFails_RollsBackSlotTransitionAndOutboxWrite()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var slot = CreateSlot(slotId);
        slot.TryHold(patientId, Now, Now.AddMinutes(5));
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(slot);
            seedContext.Patients.Add(CreatePatient(patientId));
            await seedContext.SaveChangesAsync();
        }

        var outboxEventId = Guid.Empty;
        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now.AddMinutes(1));
            var repository = new SlotRepository(context);
            var outbox = new OutboxStore(context, clock);
            var unitOfWork = new UnitOfWork(context);
            try
            {
                await unitOfWork.ExecuteAsync<int>(async cancellationToken =>
                {
                    var mutation = await repository.TryConfirmAsync(slotId, patientId, 1, clock.UtcNow,
                        cancellationToken);
                    Assert.IsNotNull(mutation);
                    var appointmentEvent = (AppointmentConfirmed)mutation!.Slot.DrainDomainEvents().Single();
                    outboxEventId = appointmentEvent.EventId;
                    await outbox.EnqueueAsync(appointmentEvent, cancellationToken);
                    throw new InvalidOperationException("Force transaction rollback.");
                }, CancellationToken.None);
                Assert.Fail("The transaction operation should throw.");
            }
            catch (InvalidOperationException exception) when (exception.Message == "Force transaction rollback.")
            {
                // The state update and pending outbox insert share this transaction.
            }
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == slotId);
        var outboxRecord = await verifyContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == outboxEventId);
        Assert.AreEqual(SlotState.Held, persisted.State);
        Assert.IsNull(outboxRecord);
    }

    [TestMethod]
    public async Task WaitlistRepository_AddAndGetNext_UsesFifoAndPreventsDuplicatePatientType()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        await RemoveFifoTestFixturesAsync();
        var type = new TestType("Ultrasound");
        var firstPatient = FifoFirstPatientId;
        var secondPatient = FifoSecondPatientId;
        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        context.Patients.AddRange(CreateFifoPatient(firstPatient), CreateFifoPatient(secondPatient));
        await context.SaveChangesAsync();
        var repository = new WaitlistRepository(context, new PostgresConstraintViolationDetector());
        var first = new WaitlistEntry(Guid.NewGuid(), firstPatient, type, Now);
        var second = new WaitlistEntry(Guid.NewGuid(), secondPatient, type, Now.AddSeconds(1));

        var firstAdded = await repository.TryAddAsync(first, CancellationToken.None);
        var secondAdded = await repository.TryAddAsync(second, CancellationToken.None);
        var duplicateAdded = await repository.TryAddAsync(
            new WaitlistEntry(Guid.NewGuid(), firstPatient, type, Now.AddSeconds(2)), CancellationToken.None);
        var next = await repository.GetNextAsync(type, CancellationToken.None);

        Assert.IsTrue(firstAdded);
        Assert.IsTrue(secondAdded);
        Assert.IsFalse(duplicateAdded);
        Assert.AreEqual(first.Id, next?.Id);
    }

    private static async Task RemoveFifoTestFixturesAsync()
    {
        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        await context.WaitlistEntries
            .Where(entry => entry.PatientId == FifoFirstPatientId || entry.PatientId == FifoSecondPatientId)
            .ExecuteDeleteAsync();
        await context.Patients
            .Where(patient => patient.Id == FifoFirstPatientId || patient.Id == FifoSecondPatientId)
            .ExecuteDeleteAsync();
    }

    private static Patient CreateFifoPatient(PatientId id) => new(id, "FIFO waitlist integration fixture", Now);

    private static Slot CreateSlot(SlotId id) =>
        new(id, new TestType("BloodTest"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30));

    private static Patient CreatePatient(PatientId id) => new(id, "Integration patient", Now);
}

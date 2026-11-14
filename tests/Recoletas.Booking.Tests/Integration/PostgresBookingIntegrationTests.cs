using Microsoft.EntityFrameworkCore;
using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.ApplicationServices;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.ApplicationServices.Outbox;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Events;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Infrastructure.Outbox;
using Recoletas.Booking.Infrastructure.Repositories;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresBookingIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
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
            .SingleOrDefaultAsync(message => message.EventType == BookingEventTypes.HoldConfirmationRequested &&
                                             message.Payload.Contains(slotId.Value.ToString()));
        Assert.AreEqual(SlotState.Held, persisted.State);
        Assert.AreEqual(patientId, persisted.AssignedPatientId);
        Assert.IsNotNull(eventRecord);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotificationEventHandler_PostgresLeasePrecision_SendsActiveNotification(bool hold)
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("notifprecision");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        var options = PostgresTestDatabase.CreateOptions(connectionString);
        var slotId = SlotId.New();
        var patientId = PatientId.New();
        var clock = new FakeClock(Now.AddTicks(7));
        await using (var seedContext = new BookingDbContext(options))
        {
            seedContext.Slots.Add(CreateSlot(slotId));
            seedContext.Patients.Add(CreatePatient(patientId));
            if (!hold)
            {
                seedContext.WaitlistEntries.Add(new WaitlistEntry(Guid.NewGuid(), patientId,
                    new TestType("BloodTest"), Now));
            }

            await seedContext.SaveChangesAsync();
        }

        await using (var context = new BookingDbContext(options))
        {
            var slots = new SlotRepository(context);
            var outbox = new OutboxStore(context, clock);
            var unitOfWork = new UnitOfWork(context);
            if (hold)
            {
                var handler = new BlockSlotHandler(slots, new PatientRepository(context), outbox,
                    unitOfWork, clock, new ApplicationServicesOptions());
                var result = await handler.Handle(new BlockSlot(slotId.Value, patientId.Value),
                    CancellationToken.None);
                Assert.IsTrue(result.Succeeded);
            }
            else
            {
                var handler = new OfferNextWaiterHandler(slots,
                    new WaitlistRepository(context, new PostgresConstraintViolationDetector()),
                    outbox, unitOfWork, clock, new ApplicationServicesOptions());
                Assert.IsTrue(await handler.Handle(new OfferNextWaiter(slotId.Value),
                    CancellationToken.None));
            }
        }

        await using var verifyContext = new BookingDbContext(options);
        var persisted = await verifyContext.Slots.AsNoTracking().SingleAsync(slot => slot.Id == slotId);
        var record = await verifyContext.OutboxMessages.AsNoTracking().SingleAsync();
        var integrationEvent = BookingEventSerializer.Deserialize(new OutboxMessage(record.Id,
            record.EventType, record.Payload, record.OccurredAtUtc));
        var sender = new Mock<INotificationSender>();
        var notificationHandler = new NotificationEventHandler(new SlotRepository(verifyContext),
            sender.Object, clock, new CapturingLogger<NotificationEventHandler>());
        DateTimeOffset eventExpiry;
        if (integrationEvent is HoldConfirmationRequested holdEvent)
        {
            eventExpiry = holdEvent.ExpiresAtUtc;
            await notificationHandler.HandleAsync(holdEvent, CancellationToken.None);
        }
        else if (integrationEvent is OfferCreated offerEvent)
        {
            eventExpiry = offerEvent.ExpiresAtUtc;
            await notificationHandler.HandleAsync(offerEvent, CancellationToken.None);
        }
        else
        {
            throw new InvalidOperationException("Expected a hold or offer notification event.");
        }

        Assert.AreEqual(clock.UtcNow.AddMinutes(5), eventExpiry);
        Assert.AreEqual(TimeSpan.FromTicks(7),
            eventExpiry - (hold ? persisted.HoldExpiresAtUtc!.Value : persisted.OfferExpiresAtUtc!.Value));
        sender.Verify(notificationSender => notificationSender.SendAsync(
            It.Is<NotificationRequest>(notification => notification.MessageId == record.Id),
            It.IsAny<CancellationToken>()), Times.Once);
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
                    var holdDomainEvent = (HoldPlaced)held!.DrainDomainEvents().Single();
                    var holdEvent = new HoldConfirmationRequested(holdDomainEvent.EventId,
                        holdDomainEvent.SlotId, holdDomainEvent.PatientId, holdDomainEvent.TestType,
                        holdDomainEvent.Generation, holdDomainEvent.ExpiresAtUtc, holdDomainEvent.OccurredAtUtc);
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
            .SingleOrDefaultAsync(message => message.EventType == BookingEventTypes.AppointmentConfirmed &&
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
                    var appointmentDomainEvent =
                        (AppointmentConfirmedEvent)mutation!.Slot.DrainDomainEvents().Single();
                    var appointmentEvent = new AppointmentConfirmed(appointmentDomainEvent.EventId,
                        appointmentDomainEvent.SlotId, appointmentDomainEvent.PatientId,
                        appointmentDomainEvent.ReservationId, appointmentDomainEvent.TestType,
                        appointmentDomainEvent.OccurredAtUtc);
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
    public async Task OutboxStore_ExpiredAndReclaimedLeases_FenceStaleCompletionAndFailure()
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("outboxlease");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        var clock = new FakeClock(Now);
        var successId = Guid.NewGuid();
        var failureId = Guid.NewGuid();
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString)))
        {
            seedContext.OutboxMessages.AddRange(
                CreateOutboxRecord(successId, clock.UtcNow),
                CreateOutboxRecord(failureId, clock.UtcNow));
            await seedContext.SaveChangesAsync();
        }

        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString));
        var store = new OutboxStore(context, clock);
        var firstLeases = await store.LeaseAsync(2, CancellationToken.None);
        var initialSuccessLease = firstLeases.Single(message => message.Id == successId);
        var initialFailureLease = firstLeases.Single(message => message.Id == failureId);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.IsFalse(await store.MarkProcessedAsync(successId, initialSuccessLease.LeaseToken,
            CancellationToken.None));
        Assert.AreEqual(OutboxFailureResult.LeaseLost,
            await store.RecordFailureAsync(failureId, initialFailureLease.LeaseToken,
                "expired attempt", TimeSpan.FromSeconds(1), 5, CancellationToken.None));

        var reclaimedLeases = await store.LeaseAsync(2, CancellationToken.None);
        var reclaimedSuccessLease = reclaimedLeases.Single(message => message.Id == successId);
        var reclaimedFailureLease = reclaimedLeases.Single(message => message.Id == failureId);
        Assert.AreNotEqual(initialSuccessLease.LeaseToken, reclaimedSuccessLease.LeaseToken);
        Assert.AreNotEqual(initialFailureLease.LeaseToken, reclaimedFailureLease.LeaseToken);

        Assert.AreEqual(OutboxFailureResult.LeaseLost,
            await store.RecordFailureAsync(successId, initialSuccessLease.LeaseToken,
                "stale failure", TimeSpan.FromSeconds(1), 5, CancellationToken.None));
        Assert.IsTrue(await store.MarkProcessedAsync(successId, reclaimedSuccessLease.LeaseToken,
            CancellationToken.None));
        Assert.AreEqual(OutboxFailureResult.RetryScheduled,
            await store.RecordFailureAsync(failureId, reclaimedFailureLease.LeaseToken,
                "current failure", TimeSpan.FromSeconds(1), 5, CancellationToken.None));
        Assert.IsFalse(await store.MarkProcessedAsync(failureId, reclaimedFailureLease.LeaseToken,
            CancellationToken.None));

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString));
        var completed = await verifyContext.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == successId);
        var retried = await verifyContext.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == failureId);
        Assert.IsNotNull(completed.ProcessedAtUtc);
        Assert.IsNull(completed.LeaseToken);
        Assert.IsNull(retried.ProcessedAtUtc);
        Assert.AreEqual("current failure", retried.LastError);
        Assert.IsNull(retried.LeaseToken);
        Assert.AreEqual(2, retried.Attempts);
    }

    [TestMethod]
    public async Task SlotRepository_OfferWithStaleReleaseGeneration_IsRejectedButCurrentGenerationSucceeds()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var slotId = SlotId.New();
        var ownerId = PatientId.New();
        var waiterId = PatientId.New();
        var slot = CreateSlot(slotId);
        Assert.IsTrue(slot.TryOffer(ownerId, Now, Now.AddMinutes(5)));
        Assert.IsTrue(slot.TryDeclineOffer(ownerId, slot.Generation, Now.AddSeconds(1)));
        Assert.AreEqual(SlotState.Available, slot.State);
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.Slots.Add(slot);
            seedContext.Patients.AddRange(CreatePatient(ownerId), CreatePatient(waiterId));
            await seedContext.SaveChangesAsync();
        }

        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var repository = new SlotRepository(context);
        var staleOffer = await repository.TryOfferAsync(slotId, waiterId, Now.AddSeconds(2), Now.AddMinutes(5),
            CancellationToken.None, expectedGeneration: 1);
        var matchingOffer = await repository.TryOfferAsync(slotId, waiterId, Now.AddSeconds(2), Now.AddMinutes(5),
            CancellationToken.None, expectedGeneration: 2);

        Assert.IsNull(staleOffer);
        Assert.IsNotNull(matchingOffer);
        Assert.AreEqual(SlotState.Offered, matchingOffer!.State);
        Assert.AreEqual(3, matchingOffer.Generation);
    }

    [TestMethod]
    public async Task WaitlistRepository_AddAndGetNext_UsesFifoAndPreventsDuplicatePatientType()
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("waitlistfifo");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        var type = new TestType("Ultrasound");
        var firstPatient = PatientId.New();
        var secondPatient = PatientId.New();
        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions(connectionString));
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

    [TestMethod]
    public async Task OfferNextWaiterHandler_ConcurrentSlotClaims_ConsumesWaiterOnlyOnce()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var testType = new TestType($"Concurrency-{Guid.NewGuid():N}");
        var waiterPatientId = PatientId.New();
        var waiter = new WaitlistEntry(Guid.NewGuid(), waiterPatientId, testType, DateTimeOffset.UnixEpoch);
        var slotIds = new[] { SlotId.New(), SlotId.New() };
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.TestTypes.Add(new TestTypeCatalogEntry(testType));
            await seedContext.SaveChangesAsync();
            seedContext.Patients.Add(CreatePatient(waiterPatientId));
            seedContext.WaitlistEntries.Add(waiter);
            seedContext.Slots.AddRange(slotIds.Select(id => new Slot(id, testType, now.AddDays(1),
                now.AddDays(1).AddMinutes(30))));
            await seedContext.SaveChangesAsync();
        }

        var selectionBarrier = new WaitlistSelectionBarrier(2);
        var attempts = slotIds.Select(async slotId =>
        {
            await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
            var repository = new WaitlistRepository(context, new PostgresConstraintViolationDetector());
            var synchronizedWaitlist = new SynchronizedWaitlistRepository(repository, selectionBarrier);
            var clock = new FakeClock(now);
            var handler = new OfferNextWaiterHandler(new SlotRepository(context), synchronizedWaitlist,
                new OutboxStore(context, clock), new UnitOfWork(context), clock, new ApplicationServicesOptions());
            return await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);
        });

        var results = await Task.WhenAll(attempts);

        selectionBarrier.AssertBothSelectedSameWaiter();
        Assert.AreEqual(1, results.Count(result => result));
        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persistedSlots = (await verifyContext.Slots.AsNoTracking().ToListAsync())
            .Where(slot => slotIds.Contains(slot.Id)).ToList();
        Assert.AreEqual(1, persistedSlots.Count(slot => slot.State == SlotState.Offered));
        Assert.AreEqual(1, persistedSlots.Count(slot => slot.State == SlotState.Available));
        var persistedWaiter = await verifyContext.WaitlistEntries.AsNoTracking().SingleAsync(entry => entry.Id == waiter.Id);
        Assert.IsFalse(persistedWaiter.IsActive);
        Assert.AreEqual(1, await verifyContext.OutboxMessages.CountAsync(message =>
            message.EventType == BookingEventTypes.OfferCreated &&
            message.Payload.Contains(waiterPatientId.Value.ToString())));
    }

    [TestMethod]
    public async Task OfferNextWaiterHandler_UnsuccessfulSlotTransition_RestoresClaim()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var testType = new TestType($"Unsuccessful-{Guid.NewGuid():N}");
        var patientId = PatientId.New();
        var waiter = new WaitlistEntry(Guid.NewGuid(), patientId, testType, Now);
        var slotId = SlotId.New();
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.TestTypes.Add(new TestTypeCatalogEntry(testType));
            await seedContext.SaveChangesAsync();
            seedContext.Patients.Add(CreatePatient(patientId));
            seedContext.WaitlistEntries.Add(waiter);
            seedContext.Slots.Add(new Slot(slotId, testType, Now.AddDays(-1), Now.AddMinutes(-1)));
            await seedContext.SaveChangesAsync();
        }

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now);
            var handler = new OfferNextWaiterHandler(new SlotRepository(context),
                new WaitlistRepository(context, new PostgresConstraintViolationDetector()), Mock.Of<IOutboxStore>(),
                new UnitOfWork(context), clock, new ApplicationServicesOptions());

            var result = await handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None);

            Assert.IsFalse(result);
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persistedWaiter = await verifyContext.WaitlistEntries.AsNoTracking().SingleAsync(entry => entry.Id == waiter.Id);
        Assert.IsTrue(persistedWaiter.IsActive);
    }

    [TestMethod]
    public async Task OfferNextWaiterHandler_OutboxEnqueueThrows_RollsBackClaimAndSlotOffer()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var testType = new TestType($"Rollback-{Guid.NewGuid():N}");
        var patientId = PatientId.New();
        var waiter = new WaitlistEntry(Guid.NewGuid(), patientId, testType, Now);
        var slotId = SlotId.New();
        await using (var seedContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            seedContext.TestTypes.Add(new TestTypeCatalogEntry(testType));
            await seedContext.SaveChangesAsync();
            seedContext.Patients.Add(CreatePatient(patientId));
            seedContext.WaitlistEntries.Add(waiter);
            seedContext.Slots.Add(new Slot(slotId, testType, Now.AddDays(1), Now.AddDays(1).AddMinutes(30)));
            await seedContext.SaveChangesAsync();
        }

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var clock = new FakeClock(Now);
            var outbox = new Mock<IOutboxStore>();
            outbox.Setup(store => store.EnqueueAsync(It.IsAny<OfferCreated>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("synthetic outbox failure"));
            var handler = new OfferNextWaiterHandler(new SlotRepository(context),
                new WaitlistRepository(context, new PostgresConstraintViolationDetector()), outbox.Object,
                new UnitOfWork(context), clock, new ApplicationServicesOptions());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                handler.Handle(new OfferNextWaiter(slotId.Value), CancellationToken.None));
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persistedWaiter = await verifyContext.WaitlistEntries.AsNoTracking().SingleAsync(entry => entry.Id == waiter.Id);
        var persistedSlot = await verifyContext.Slots.AsNoTracking().SingleAsync(slot => slot.Id == slotId);
        Assert.IsTrue(persistedWaiter.IsActive);
        Assert.AreEqual(SlotState.Available, persistedSlot.State);
        Assert.IsNull(persistedSlot.AssignedPatientId);
    }

    private sealed class WaitlistSelectionBarrier(int arrivals)
    {
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentBag<Guid> _selectedEntryIds = [];
        private int _arrived;

        public async Task WaitAsync(Guid entryId, CancellationToken cancellationToken)
        {
            _selectedEntryIds.Add(entryId);
            if (Interlocked.Increment(ref _arrived) == arrivals)
            {
                _released.TrySetResult();
            }

            await _released.Task.WaitAsync(cancellationToken);
        }

        public void AssertBothSelectedSameWaiter() =>
            Assert.AreEqual(1, _selectedEntryIds.Distinct().Count());
    }

    private sealed class SynchronizedWaitlistRepository(IWaitlistRepository inner,
        WaitlistSelectionBarrier selectionBarrier) : IWaitlistRepository
    {
        public Task<bool> TryAddAsync(WaitlistEntry entry, CancellationToken cancellationToken) =>
            inner.TryAddAsync(entry, cancellationToken);

        public Task<WaitlistEntry?> GetActiveEntryAsync(PatientId patientId, TestType testType,
            CancellationToken cancellationToken) =>
            inner.GetActiveEntryAsync(patientId, testType, cancellationToken);

        public async Task<WaitlistEntry?> GetNextAsync(TestType testType, CancellationToken cancellationToken)
        {
            var waiter = await inner.GetNextAsync(testType, cancellationToken);
            if (waiter is not null)
            {
                await selectionBarrier.WaitAsync(waiter.Id, cancellationToken);
            }

            return waiter;
        }

        public Task<bool> TryClaimAsync(Guid entryId, CancellationToken cancellationToken) =>
            inner.TryClaimAsync(entryId, cancellationToken);

        public Task<bool> TryRestoreClaimAsync(Guid entryId, CancellationToken cancellationToken) =>
            inner.TryRestoreClaimAsync(entryId, cancellationToken);

        public Task RemoveAsync(Guid entryId, CancellationToken cancellationToken) =>
            inner.RemoveAsync(entryId, cancellationToken);
    }

    private static Patient CreateFifoPatient(PatientId id) => new(id, "FIFO waitlist integration fixture", Now);

    private static Slot CreateSlot(SlotId id) =>
        new(id, new TestType("BloodTest"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30));

    private static Patient CreatePatient(PatientId id) => new(id, "Integration patient", Now);

    private static OutboxRecord CreateOutboxRecord(Guid id, DateTimeOffset now) => new()
    {
        Id = id,
        EventType = BookingEventTypes.SlotReleased,
        Payload = "{}",
        OccurredAtUtc = now,
        AvailableAtUtc = now
    };
}

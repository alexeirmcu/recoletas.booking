using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.ApplicationServices.Validators;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Repositories;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class PersistenceIntegrityTests
{
    private static readonly DateTimeOffset Now = new(2035, 4, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DeletePatient_WhenReferencedBySlotReservationAndWaitlist_IsRestricted()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var patientId = PatientId.New();
        var slotId = SlotId.New();
        var slot = CreateSlot(slotId);
        Assert.IsTrue(slot.TryHold(patientId, Now, Now.AddMinutes(5)));

        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        context.Patients.Add(CreatePatient(patientId));
        context.Slots.Add(slot);
        context.Reservations.Add(Reservation.Create(slotId, patientId, Now));
        context.WaitlistEntries.Add(new WaitlistEntry(Guid.NewGuid(), patientId, new TestType("MRI"), Now));
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(async () =>
            await context.Patients.Where(patient => patient.Id == patientId).ExecuteDeleteAsync());

        Assert.AreEqual(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [TestMethod]
    public async Task DeleteSlot_WhenReferencedByReservation_IsRestricted()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var patientId = PatientId.New();
        var slotId = SlotId.New();

        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        context.Patients.Add(CreatePatient(patientId));
        context.Slots.Add(CreateSlot(slotId));
        context.Reservations.Add(Reservation.Create(slotId, patientId, Now));
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(async () =>
            await context.Slots.Where(slot => slot.Id == slotId).ExecuteDeleteAsync());

        Assert.AreEqual(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [TestMethod]
    public async Task SavePatient_WithDeletedAtUtc_PersistsTimestamp()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var patientId = PatientId.New();
        var deletedAtUtc = new DateTimeOffset(2035, 4, 6, 1, 2, 3, TimeSpan.FromHours(3));

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            context.Patients.Add(new Patient(patientId, "Deleted test patient", Now, deletedAtUtc: deletedAtUtc));
            await context.SaveChangesAsync();
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Patients.AsNoTracking().SingleAsync(patient => patient.Id == patientId);

        Assert.AreEqual(deletedAtUtc.ToUniversalTime(), persisted.DeletedAtUtc);
    }

    [TestMethod]
    public async Task RegisterPatient_WithPaddedNhc_PersistsTrimmedValue()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var nhc = $"NHC-{Guid.NewGuid():N}";
        var patientId = Guid.Empty;

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var handler = new RegisterPatientHandler(new PatientRepository(context), new FakeClock(Now));
            var result = await handler.Handle(
                new RegisterPatient(new RegisterPatientRequest("NHC test patient", $"  {nhc}\t ")),
                CancellationToken.None);
            patientId = result.PatientId;

            Assert.AreEqual(nhc, result.Nhc);
        }

        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var persisted = await verifyContext.Patients.AsNoTracking()
            .SingleAsync(patient => patient.Id == new PatientId(patientId));

        Assert.AreEqual(nhc, persisted.Nhc);
    }

    [TestMethod]
    public async Task RegisterPatient_WithWhitespaceOnlyNhc_AllowsMultipleNullValues()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var handler = new RegisterPatientHandler(new PatientRepository(context), new FakeClock(Now));

        var first = await handler.Handle(
            new RegisterPatient(new RegisterPatientRequest("Patient without NHC", null)), CancellationToken.None);
        var second = await handler.Handle(
            new RegisterPatient(new RegisterPatientRequest("Patient with blank NHC", " \t\r\n ")),
            CancellationToken.None);

        Assert.IsNull(first.Nhc);
        Assert.IsNull(second.Nhc);
        Assert.AreNotEqual(first.PatientId, second.PatientId);

        var persistedPatients = await context.Patients.AsNoTracking()
            .Where(patient => patient.Id == new PatientId(first.PatientId) || patient.Id == new PatientId(second.PatientId))
            .ToListAsync();
        Assert.AreEqual(2, persistedPatients.Count);
        Assert.IsTrue(persistedPatients.All(patient => patient.Nhc is null));
    }

    [TestMethod]
    public async Task RegisterPatient_ConcurrentDuplicateNhc_AllowsOnlyOnePatient()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var nhc = $"NHC-{Guid.NewGuid():N}";
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> TryRegisterAsync()
        {
            await start.Task;
            await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
            var handler = new RegisterPatientHandler(new PatientRepository(context), new FakeClock(Now));
            try
            {
                await handler.Handle(
                    new RegisterPatient(new RegisterPatientRequest("Concurrent NHC patient", nhc)),
                    CancellationToken.None);
                return true;
            }
            catch (PatientNhcAlreadyExistsException)
            {
                return false;
            }
        }

        var registrations = new[] { TryRegisterAsync(), TryRegisterAsync() };
        start.SetResult(true);
        var results = await Task.WhenAll(registrations);

        Assert.AreEqual(1, results.Count(succeeded => succeeded));
        await using var verifyContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        Assert.AreEqual(1, await verifyContext.Patients.CountAsync(patient => patient.Nhc == nhc));
    }

    [TestMethod]
    public async Task PatientRepository_WhenOtherUniqueConstraintFails_DoesNotReportNhcConflict()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var patientId = PatientId.New();
        var originalNhc = $"NHC-{Guid.NewGuid():N}";

        await using (var context = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var repository = new PatientRepository(context);
            await repository.AddAsync(new Patient(patientId, "Original patient", Now, originalNhc),
                CancellationToken.None);
        }

        await using var duplicateContext = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        var duplicateRepository = new PatientRepository(duplicateContext);

        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => duplicateRepository.AddAsync(
            new Patient(patientId, "Different patient", Now, $"{originalNhc}-DIFFERENT"), CancellationToken.None));
    }

    [TestMethod]
    public async Task UnsupportedTestType_IsRejectedByValidatorsAndCatalogForeignKeys()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        var queryResult = await new QueryAgendaValidator().ValidateAsync(
            new QueryAgenda("CT", Now, Now.AddDays(1)));
        var waitlistResult = await new JoinWaitlistValidator().ValidateAsync(
            new JoinWaitlist(Guid.NewGuid(), new JoinWaitlistRequest("CT")));

        Assert.AreEqual(BookingErrorCodes.UnsupportedTestType, queryResult.Errors.Single().ErrorCode);
        Assert.AreEqual(BookingErrorCodes.UnsupportedTestType, waitlistResult.Errors.Single().ErrorCode);

        await using (var slotContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            slotContext.Slots.Add(new Slot(SlotId.New(), new TestType("CT"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30)));
            await Assert.ThrowsExceptionAsync<DbUpdateException>(() => slotContext.SaveChangesAsync());
        }

        await using (var waitlistContext = new BookingDbContext(PostgresTestDatabase.CreateOptions()))
        {
            var patientId = PatientId.New();
            waitlistContext.Patients.Add(CreatePatient(patientId));
            waitlistContext.WaitlistEntries.Add(
                new WaitlistEntry(Guid.NewGuid(), patientId, new TestType("CT"), Now));
            await Assert.ThrowsExceptionAsync<DbUpdateException>(() => waitlistContext.SaveChangesAsync());
        }
    }

    private static Patient CreatePatient(PatientId id) => new(id, "Integration patient", Now);

    private static Slot CreateSlot(SlotId id) =>
        new(id, new TestType("BloodTest"), Now.AddDays(1), Now.AddDays(1).AddMinutes(30));
}

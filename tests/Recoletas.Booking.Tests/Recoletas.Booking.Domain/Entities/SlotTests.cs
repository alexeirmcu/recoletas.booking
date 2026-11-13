using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Domain.Entities;

[TestClass]
[TestCategory("Unit")]
public sealed class SlotTests
{
    private static readonly TimeSpan LeaseWindow = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void TryHold_WhenSlotIsAvailable_SetsHeldStateAndExpiry()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();

        var held = slot.TryHold(patient, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        Assert.IsTrue(held);
        Assert.AreEqual(SlotState.Held, slot.State);
        Assert.AreEqual(patient, slot.AssignedPatientId);
        Assert.AreEqual(clock.UtcNow.Add(LeaseWindow), slot.HoldExpiresAtUtc);
        Assert.AreEqual(1, slot.Generation);
        var holdRequest = (HoldConfirmationRequested)slot.DomainEvents.Single();
        Assert.AreEqual(slot.Id.Value, holdRequest.SlotId);
        Assert.AreEqual(patient.Value, holdRequest.PatientId);
        Assert.AreEqual(slot.TestType.Value, holdRequest.TestType);
        Assert.AreEqual(slot.Generation, holdRequest.Generation);
        Assert.AreEqual(slot.HoldExpiresAtUtc, holdRequest.ExpiresAtUtc);
        Assert.AreEqual(clock.UtcNow, holdRequest.OccurredAtUtc);
        Assert.AreNotEqual(Guid.Empty, holdRequest.EventId);
    }

    [TestMethod]
    public void TryHold_WhenSlotIsAlreadyHeld_RejectsSecondPatient()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var firstPatient = PatientId.New();
        slot.TryHold(firstPatient, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));
        var generation = slot.Generation;

        var held = slot.TryHold(PatientId.New(), clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        Assert.IsFalse(held);
        Assert.AreEqual(firstPatient, slot.AssignedPatientId);
        Assert.AreEqual(generation, slot.Generation);
        Assert.AreEqual(1, slot.DomainEvents.Count);
    }

    [TestMethod]
    public void TryHold_WhenLeaseExpiredButWindowIsFuture_ReclaimsSlotAndAdvancesGeneration()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        slot.TryHold(PatientId.New(), clock.UtcNow, clock.UtcNow.AddMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));

        var replacement = PatientId.New();
        var held = slot.TryHold(replacement, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        Assert.IsTrue(held);
        Assert.AreEqual(replacement, slot.AssignedPatientId);
        Assert.AreEqual(3, slot.Generation);
    }

    [TestMethod]
    public void TryHold_WhenSlotWindowHasExpired_RejectsClaim()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock, clock.UtcNow.AddMinutes(-20), clock.UtcNow);

        var held = slot.TryHold(PatientId.New(), clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        Assert.IsFalse(held);
        Assert.AreEqual(SlotState.Available, slot.State);
    }

    [TestMethod]
    public void TryConfirmHold_WhenPatientOwnsUnexpiredHold_ConfirmsAndRaisesAppointmentEvent()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();
        slot.TryHold(patient, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        var reservation = slot.TryConfirmHold(patient, clock.UtcNow.AddMinutes(1));

        Assert.IsNotNull(reservation);
        Assert.AreEqual(SlotState.Confirmed, slot.State);
        Assert.AreEqual(reservation.Id, slot.CurrentReservationId);
        Assert.IsNull(slot.HoldExpiresAtUtc);
        Assert.AreEqual(2, slot.Generation);
        CollectionAssert.AreEquivalent(new[] { typeof(HoldConfirmationRequested), typeof(AppointmentConfirmed) },
            slot.DomainEvents.Select(integrationEvent => integrationEvent.GetType()).ToArray());
    }

    [TestMethod]
    public void TryConfirmHold_WhenHoldExpired_ReturnsNullAndLeavesLeaseForLazyRelease()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();
        slot.TryHold(patient, clock.UtcNow, clock.UtcNow.AddMinutes(1));

        var reservation = slot.TryConfirmHold(patient, clock.UtcNow.AddMinutes(1));

        Assert.IsNull(reservation);
        Assert.AreEqual(SlotState.Held, slot.State);
        Assert.IsTrue(slot.IsAvailableAt(clock.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public void TryCancel_WhenReservationMatchesConfirmedAppointment_ReleasesSlot()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();
        slot.TryHold(patient, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));
        var reservation = slot.TryConfirmHold(patient, clock.UtcNow.AddMinutes(1))!;

        var cancelled = slot.TryCancel(patient, reservation.Id, clock.UtcNow.AddMinutes(2));

        Assert.IsTrue(cancelled);
        Assert.AreEqual(SlotState.Available, slot.State);
        Assert.IsNull(slot.AssignedPatientId);
        Assert.AreEqual(3, slot.Generation);
        CollectionAssert.AreEquivalent(new[]
        {
            typeof(HoldConfirmationRequested), typeof(AppointmentConfirmed), typeof(SlotReleased)
        }, slot.DomainEvents.Select(integrationEvent => integrationEvent.GetType()).ToArray());
    }

    [TestMethod]
    public void TryOfferAndTryAcceptOffer_WhenOfferIsCurrent_ConfirmsForWaiter()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();
        var offered = slot.TryOffer(patient, clock.UtcNow, clock.UtcNow.Add(LeaseWindow));
        var offerGeneration = slot.Generation;

        var reservation = slot.TryAcceptOffer(patient, offerGeneration, clock.UtcNow.AddMinutes(1));

        Assert.IsTrue(offered);
        Assert.AreEqual(SlotState.Confirmed, slot.State);
        Assert.IsNotNull(reservation);
        Assert.AreEqual(offerGeneration + 1, slot.Generation);
    }

    [TestMethod]
    public void TryOffer_WhenSlotAlreadyLeased_RejectsOffer()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        slot.TryHold(PatientId.New(), clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        var offered = slot.TryOffer(PatientId.New(), clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        Assert.IsFalse(offered);
        Assert.AreEqual(SlotState.Held, slot.State);
    }

    [TestMethod]
    public void TryAcceptOffer_WhenOfferExpiredOrGenerationStale_RejectsAcceptance()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        var patient = PatientId.New();
        slot.TryOffer(patient, clock.UtcNow, clock.UtcNow.AddMinutes(1));
        var generation = slot.Generation;

        var stale = slot.TryAcceptOffer(patient, generation - 1, clock.UtcNow);
        var expired = slot.TryAcceptOffer(patient, generation, clock.UtcNow.AddMinutes(1));

        Assert.IsNull(stale);
        Assert.IsNull(expired);
        Assert.AreEqual(SlotState.Offered, slot.State);
    }

    [TestMethod]
    public void IsAvailableAt_WhenOfferExpired_ReportsAvailableWithoutChangingState()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        slot.TryOffer(PatientId.New(), clock.UtcNow, clock.UtcNow.AddMinutes(1));

        var available = slot.IsAvailableAt(clock.UtcNow.AddMinutes(1));

        Assert.IsTrue(available);
        Assert.AreEqual(SlotState.Offered, slot.State);
    }

    [TestMethod]
    public void DrainDomainEvents_WhenCalledTwice_ReturnsEventsOnlyOnce()
    {
        var clock = CreateClock();
        var slot = CreateSlot(clock);
        slot.TryOffer(PatientId.New(), clock.UtcNow, clock.UtcNow.Add(LeaseWindow));

        var firstDrain = slot.DrainDomainEvents();
        var secondDrain = slot.DrainDomainEvents();

        Assert.AreEqual(1, firstDrain.Count);
        Assert.AreEqual(0, secondDrain.Count);
    }

    private static FakeClock CreateClock() => new(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static Slot CreateSlot(FakeClock clock, DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null) =>
        new(SlotId.New(), new TestType("BloodTest"), startsAt ?? clock.UtcNow.AddDays(1),
            endsAt ?? clock.UtcNow.AddDays(1).AddMinutes(30));
}

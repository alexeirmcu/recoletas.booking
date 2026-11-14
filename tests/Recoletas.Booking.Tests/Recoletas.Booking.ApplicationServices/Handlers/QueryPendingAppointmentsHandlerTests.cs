using Moq;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class QueryPendingAppointmentsHandlerTests
{
    [TestMethod]
    public async Task Handle_ActiveHoldsAndOffers_MapsCurrentDetailsAndUsesSingleUtcClockSnapshot()
    {
        var patientId = PatientId.New();
        var now = new DateTimeOffset(2030, 5, 6, 12, 0, 0, TimeSpan.FromHours(2));
        var hold = CreateSlot(patientId, SlotState.Held, now.ToUniversalTime().AddHours(2),
            now.ToUniversalTime().AddMinutes(5), now.ToUniversalTime());
        var offer = CreateSlot(patientId, SlotState.Offered, now.ToUniversalTime().AddHours(3),
            now.ToUniversalTime().AddMinutes(10), now.ToUniversalTime());
        var clock = new FakeClock(now);
        var slots = new Mock<ISlotRepository>(MockBehavior.Strict);
        slots.Setup(repository => repository.ListPendingForPatientAsync(patientId, now.ToUniversalTime(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([hold, offer]);
        var handler = new QueryPendingAppointmentsHandler(slots.Object, clock);

        var result = await handler.Handle(new QueryPendingAppointments(patientId.Value), CancellationToken.None);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(hold.Id.Value, result[0].SlotId);
        Assert.AreEqual("BloodTest", result[0].TestType);
        Assert.AreEqual(hold.StartsAtUtc, result[0].StartsAtUtc);
        Assert.AreEqual(hold.EndsAtUtc, result[0].EndsAtUtc);
        Assert.AreEqual("hold", result[0].Kind);
        Assert.AreEqual(1, result[0].Generation);
        Assert.AreEqual(hold.HoldExpiresAtUtc, result[0].ExpiresAtUtc);
        Assert.AreEqual(offer.Id.Value, result[1].SlotId);
        Assert.AreEqual("offer", result[1].Kind);
        Assert.AreEqual(1, result[1].Generation);
        Assert.AreEqual(offer.OfferExpiresAtUtc, result[1].ExpiresAtUtc);
        slots.Verify(repository => repository.ListPendingForPatientAsync(patientId,
            now.ToUniversalTime(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Handle_NoPendingSlots_ReturnsEmptyList()
    {
        var patientId = PatientId.New();
        var now = DateTimeOffset.UtcNow;
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.ListPendingForPatientAsync(patientId, now,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var handler = new QueryPendingAppointmentsHandler(slots.Object, new FakeClock(now));

        var result = await handler.Handle(new QueryPendingAppointments(patientId.Value), CancellationToken.None);

        Assert.AreEqual(0, result.Count);
    }

    private static Slot CreateSlot(PatientId patientId, SlotState state, DateTimeOffset startsAtUtc,
        DateTimeOffset expiresAtUtc, DateTimeOffset transitionAtUtc)
    {
        var slot = new Slot(SlotId.New(), new TestType("BloodTest"), startsAtUtc, startsAtUtc.AddMinutes(30));
        if (state == SlotState.Held)
        {
            Assert.IsTrue(slot.TryHold(patientId, transitionAtUtc, expiresAtUtc));
        }
        else
        {
            Assert.IsTrue(slot.TryOffer(patientId, transitionAtUtc, expiresAtUtc));
        }

        return slot;
    }
}

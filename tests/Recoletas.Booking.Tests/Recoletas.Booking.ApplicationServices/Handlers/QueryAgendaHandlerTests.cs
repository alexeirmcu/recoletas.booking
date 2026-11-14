using Moq;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class QueryAgendaHandlerTests
{
    [TestMethod]
    public async Task Handle_FilteredQuery_ReturnsOnlyFutureAvailableSlotsInRange()
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var wanted = CreateSlot("BloodTest", now.AddDays(2));
        var otherType = CreateSlot("Ultrasound", now.AddDays(2).AddHours(1));
        var outsideRange = CreateSlot("BloodTest", now.AddDays(4));
        var held = CreateSlot("BloodTest", now.AddDays(2).AddHours(2));
        held.TryHold(PatientId.New(), now, now.AddMinutes(5));
        var expired = new Slot(SlotId.New(), new TestType("BloodTest"), now.AddDays(-2), now.AddDays(-1));
        var slots = new Mock<ISlotRepository>();
        slots.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { outsideRange, otherType, held, expired, wanted });
        var handler = new QueryAgendaHandler(slots.Object, clock);

        var result = await handler.Handle(new QueryAgenda("BloodTest", now.AddDays(1), now.AddDays(3)),
            CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(wanted.Id.Value, result[0].SlotId);
        Assert.AreEqual("BloodTest", result[0].TestType);
        Assert.AreEqual(wanted.StartsAtUtc, result[0].StartsAtUtc);
        Assert.AreEqual(wanted.EndsAtUtc, result[0].EndsAtUtc);
        Assert.AreEqual("Available", result[0].State);
        Assert.IsNull(typeof(SlotSummary).GetProperty("Generation"));
    }

    [TestMethod]
    public async Task Handle_InvalidDateRange_ReturnsEmptyWithoutListingSlots()
    {
        var slots = new Mock<ISlotRepository>(MockBehavior.Strict);
        var handler = new QueryAgendaHandler(slots.Object, new FakeClock(DateTimeOffset.UtcNow));
        var now = DateTimeOffset.UtcNow;

        var result = await handler.Handle(new QueryAgenda(null, now, now), CancellationToken.None);

        Assert.AreEqual(0, result.Count);
        slots.VerifyNoOtherCalls();
    }

    private static Slot CreateSlot(string testType, DateTimeOffset start) =>
        new(SlotId.New(), new TestType(testType), start, start.AddMinutes(30));
}

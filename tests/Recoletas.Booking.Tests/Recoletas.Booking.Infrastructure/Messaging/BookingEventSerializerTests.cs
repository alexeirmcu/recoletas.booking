using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Infrastructure.Messaging;

namespace Recoletas.Booking.Tests.Recoletas.Booking.Infrastructure.Messaging;

[TestClass]
[TestCategory("Unit")]
public sealed class BookingEventSerializerTests
{
    [TestMethod]
    public void Deserialize_LegacyOutboxTypeAndPayload_RemainsReadable()
    {
        var eventId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var message = new OutboxMessage(eventId, BookingEventTypes.LegacySlotReleased,
            $"{{\"eventId\":\"{eventId}\",\"slotId\":\"{slotId}\",\"testType\":\"BloodTest\",\"generation\":7," +
            "\"occurredAtUtc\":\"2030-01-01T12:00:00+00:00\"}",
            DateTimeOffset.Parse("2030-01-01T12:00:00Z"));

        var integrationEvent = BookingEventSerializer.Deserialize(message);

        var released = integrationEvent as SlotReleased;
        Assert.IsNotNull(released);
        Assert.AreEqual(eventId, released!.EventId);
        Assert.AreEqual(slotId, released.SlotId);
        Assert.AreEqual(7, released.Generation);
        Assert.AreEqual(BookingEventTypes.LegacySlotReleased,
            RabbitMqTopology.RoutingKey(BookingEventTypes.LegacySlotReleased));
        Assert.AreEqual(BookingEventTypes.SlotReleased,
            RabbitMqTopology.RoutingKey(BookingEventTypes.SlotReleased));
    }

    [TestMethod]
    public void Deserialize_UnknownEventType_ThrowsExplicitly()
    {
        var message = new OutboxMessage(Guid.NewGuid(), "booking.unknown.v1", "{}",
            DateTimeOffset.UtcNow);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => BookingEventSerializer.Deserialize(message));

        StringAssert.Contains(exception.Message, "Unknown booking event type");
    }
}

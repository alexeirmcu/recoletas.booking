using System.Text.Json;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.Infrastructure.Messaging;

public static class BookingEventSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, Type> EventTypes = new Dictionary<string, Type>(
        StringComparer.Ordinal)
    {
        [BookingEventTypes.HoldConfirmationRequested] = typeof(HoldConfirmationRequested),
        [BookingEventTypes.SlotReleased] = typeof(SlotReleased),
        [BookingEventTypes.OfferCreated] = typeof(OfferCreated),
        [BookingEventTypes.AppointmentConfirmed] = typeof(AppointmentConfirmed)
    };

    public static object Deserialize(OutboxMessage message)
    {
        if (!EventTypes.TryGetValue(message.EventType, out var eventType))
        {
            throw new InvalidOperationException($"Unknown booking event type '{message.EventType}'.");
        }

        return JsonSerializer.Deserialize(message.Payload, eventType, SerializerOptions)
               ?? throw new InvalidOperationException($"Booking payload for '{message.EventType}' was empty.");
    }
}

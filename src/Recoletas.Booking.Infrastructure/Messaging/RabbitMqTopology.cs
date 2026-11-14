using RabbitMQ.Client;
using Recoletas.Booking.Contracts.Events;

namespace Recoletas.Booking.Infrastructure.Messaging;

public static class RabbitMqTopology
{
    public const string Exchange = "booking.events";
    public const string WorkflowQueue = "booking.workflow";
    public const string NotificationQueue = "booking.notifications";

    public static string RoutingKey(string eventType) => eventType switch
    {
        BookingEventTypes.SlotReleased or BookingEventTypes.HoldConfirmationRequested or
            BookingEventTypes.OfferCreated or BookingEventTypes.AppointmentConfirmed or
            BookingEventTypes.LegacySlotReleased or BookingEventTypes.LegacyHoldConfirmationRequested or
            BookingEventTypes.LegacyOfferCreated or BookingEventTypes.LegacyAppointmentConfirmed => eventType,
        _ => throw new InvalidOperationException($"Unsupported booking event '{eventType}'.")
    };

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken token)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: token);
        foreach (var queue in new[] { WorkflowQueue, NotificationQueue })
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    // No implicit broker delivery limit: application failures use the explicit retry/DLQ path.
                    ["x-delivery-limit"] = -1
                }, cancellationToken: token);
            await channel.QueueDeclareAsync(queue + ".dlq", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
                cancellationToken: token);
            await channel.QueueDeclareAsync(queue + ".retry", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                    ["x-message-ttl"] = (int)options.RetryDelay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queue
                }, cancellationToken: token);
        }

        foreach (var key in new[]
                 {
                     BookingEventTypes.LegacySlotReleased, BookingEventTypes.SlotReleased
                 })
        {
            await channel.QueueBindAsync(WorkflowQueue, Exchange, key, cancellationToken: token);
        }

        foreach (var key in new[]
                 {
                     BookingEventTypes.LegacyHoldConfirmationRequested,
                     BookingEventTypes.LegacyOfferCreated,
                     BookingEventTypes.LegacyAppointmentConfirmed,
                     BookingEventTypes.HoldConfirmationRequested,
                     BookingEventTypes.OfferCreated,
                     BookingEventTypes.AppointmentConfirmed
                 })
        {
            await channel.QueueBindAsync(NotificationQueue, Exchange, key, cancellationToken: token);
        }
    }
}

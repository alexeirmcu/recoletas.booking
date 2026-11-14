using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.Contracts.Events;

namespace Recoletas.Booking.Infrastructure.Messaging;

public sealed class RabbitMqConsumersHostedService(RabbitMqConnection connection, RabbitMqOptions options,
    IServiceScopeFactory scopes, RabbitMqDeliveryHandler deliveries,
    ILogger<RabbitMqConsumersHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ConsumeAsync(RabbitMqTopology.WorkflowQueue, stoppingToken),
            ConsumeAsync(RabbitMqTopology.NotificationQueue, stoppingToken));

    private async Task ConsumeAsync(string queue, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var channel = await connection.CreateChannelAsync(token);
                await channel.BasicQosAsync(0, 1, global: false, cancellationToken: token);
                var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    failed.TrySetException(new InvalidOperationException("RabbitMQ consumer channel closed."));
                    return Task.CompletedTask;
                };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) =>
                {
                    failed.TrySetException(new InvalidOperationException("RabbitMQ consumer was cancelled."));
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await deliveries.HandleAsync(channel, queue, delivery, async (message, handlerToken) =>
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            var integrationEvent = BookingEventSerializer.Deserialize(message);
                            if (integrationEvent is SlotReleased released &&
                                queue == RabbitMqTopology.WorkflowQueue)
                            {
                                await scope.ServiceProvider.GetRequiredService<SlotReleasedEventHandler>()
                                    .HandleAsync(released, handlerToken);
                            }
                            else if (queue == RabbitMqTopology.NotificationQueue)
                            {
                                var handler = scope.ServiceProvider.GetRequiredService<NotificationEventHandler>();
                                switch (integrationEvent)
                                {
                                    case HoldConfirmationRequested hold:
                                        await handler.HandleAsync(hold, handlerToken);
                                        break;
                                    case OfferCreated offer:
                                        await handler.HandleAsync(offer, handlerToken);
                                        break;
                                    case AppointmentConfirmed confirmed:
                                        await handler.HandleAsync(confirmed, handlerToken);
                                        break;
                                    default:
                                        throw new InvalidOperationException(
                                            $"Unsupported notification event '{integrationEvent.GetType().Name}'.");
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException(
                                    $"Event '{integrationEvent.GetType().Name}' is not supported on queue '{queue}'.");
                            }
                        }, token);
                    }
                    catch (Exception exception)
                    {
                        // Forwarding/ack failure must terminate this channel, not silently lose a delivery.
                        failed.TrySetException(exception);
                    }
                };
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer: consumer, cancellationToken: token);
                await failed.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("RabbitMQ consumer {Queue} stopped with {FailureType}; reconnecting.",
                    queue, exception.GetType().Name);
            }

            await Task.Delay(options.ReconnectDelay, token);
        }
    }
}

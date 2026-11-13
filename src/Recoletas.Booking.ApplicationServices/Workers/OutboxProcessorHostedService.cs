using Recoletas.Booking.ApplicationServices.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Recoletas.Booking.ApplicationServices.Workers;

public sealed class OutboxProcessorHostedService(IServiceScopeFactory scopeFactory,
    IOptions<OutboxProcessorOptions> options, ILogger<OutboxProcessorHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollInterval > TimeSpan.Zero
            ? options.Value.PollInterval
            : TimeSpan.FromSeconds(1);
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox polling iteration failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

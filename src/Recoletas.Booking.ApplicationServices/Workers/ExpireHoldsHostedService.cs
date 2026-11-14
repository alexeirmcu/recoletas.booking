using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Recoletas.Booking.ApplicationServices.Workers;

public sealed class ExpireHoldsHostedService(IServiceScopeFactory scopeFactory,
    IOptions<ExpireHoldsOptions> options, ILogger<ExpireHoldsHostedService> logger) : BackgroundService
{
    public async Task<int> ExpireExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var slots = services.GetRequiredService<ISlotRepository>();
        var mediator = services.GetRequiredService<IMediator>();
        var clock = services.GetRequiredService<IClock>();
        var now = clock.UtcNow.ToUniversalTime();
        var expired = (await slots.ListAsync(cancellationToken))
            .Where(slot => slot.State == SlotState.Held && slot.HoldExpiresAtUtc <= now ||
                           slot.State == SlotState.Offered && slot.OfferExpiresAtUtc <= now)
            .Select(slot => slot.Id)
            .ToArray();

        var count = 0;
        foreach (var slotId in expired)
        {
            if (await mediator.Send(new ExpireSlotLease(slotId.Value), cancellationToken))
            {
                count++;
            }
        }

        return count;
    }

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
                await ExpireExpiredLeasesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Expired slot lease scan failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

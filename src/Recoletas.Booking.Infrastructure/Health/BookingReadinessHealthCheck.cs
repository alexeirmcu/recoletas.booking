using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Messaging;

namespace Recoletas.Booking.Infrastructure.Health;

public sealed class BookingReadinessHealthCheck(
    BookingDbContext database, RabbitMqConnection broker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!broker.IsConnected)
        {
            return HealthCheckResult.Unhealthy("The broker connection is unavailable.");
        }

        return await database.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database is unavailable.");
    }
}

using Recoletas.Booking.Domain.Services;

namespace Recoletas.Booking.Infrastructure.Clock;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

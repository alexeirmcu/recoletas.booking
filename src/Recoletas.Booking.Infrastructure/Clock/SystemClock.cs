using Recoletas.Booking.ApplicationServices.Abstractions;

namespace Recoletas.Booking.Infrastructure.Clock;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

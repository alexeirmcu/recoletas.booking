using Recoletas.Booking.ApplicationServices.Abstractions;

namespace Recoletas.Booking.Tests.TestSupport;

public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow.ToUniversalTime();

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

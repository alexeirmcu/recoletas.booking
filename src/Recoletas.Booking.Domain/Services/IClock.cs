namespace Recoletas.Booking.Domain.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

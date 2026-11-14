namespace Recoletas.Booking.ApplicationServices.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

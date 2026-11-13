namespace Recoletas.Booking.ApplicationServices.Workers;

public sealed class ExpireHoldsOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
}

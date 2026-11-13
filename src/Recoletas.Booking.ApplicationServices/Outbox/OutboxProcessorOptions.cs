namespace Recoletas.Booking.ApplicationServices.Outbox;

public sealed class OutboxProcessorOptions
{
    public int BatchSize { get; set; } = 20;

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class OutboxRecord
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset AvailableAtUtc { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }

    public DateTimeOffset? DeadAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

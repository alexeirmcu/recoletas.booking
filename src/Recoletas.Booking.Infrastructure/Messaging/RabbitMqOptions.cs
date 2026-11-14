namespace Recoletas.Booking.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public string TlsServerName { get; set; } = "";
    public int MaxConsumerAttempts { get; set; } = 5;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
        if (Port is < 1 or > 65535 || MaxConsumerAttempts < 1 || RetryDelay <= TimeSpan.Zero ||
            RetryDelay.TotalMilliseconds > int.MaxValue || ReconnectDelay <= TimeSpan.Zero ||
            ConfirmTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("RabbitMQ port, attempt limit and delays must be valid positive values.");
        }
    }
}

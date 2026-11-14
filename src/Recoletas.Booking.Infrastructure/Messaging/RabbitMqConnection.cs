using RabbitMQ.Client;
using System.Net.Security;
using System.Security.Authentication;

namespace Recoletas.Booking.Infrastructure.Messaging;

public sealed class RabbitMqConnection(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IChannel> CreateChannelAsync(CancellationToken token)
    {
        options.Validate();
        await _gate.WaitAsync(token);
        try
        {
            if (_connection is null || !_connection.IsOpen)
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                }

                _connection = await new ConnectionFactory
                {
                    HostName = options.HostName,
                    Port = options.Port,
                    VirtualHost = options.VirtualHost,
                    UserName = options.UserName,
                    Password = options.Password,
                    Ssl = new SslOption
                    {
                        Enabled = options.UseTls,
                        ServerName = string.IsNullOrWhiteSpace(options.TlsServerName)
                            ? options.HostName : options.TlsServerName,
                        Version = SslProtocols.None,
                        AcceptablePolicyErrors = SslPolicyErrors.None
                    },
                    AutomaticRecoveryEnabled = false,
                    ConsumerDispatchConcurrency = 1
                }.CreateConnectionAsync(token);
            }

            // BasicPublishAsync awaits ack and throws on nack or mandatory return.
            var channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true), token);
            try
            {
                await RabbitMqTopology.DeclareAsync(channel, options, token);
                return channel;
            }
            catch
            {
                await channel.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}

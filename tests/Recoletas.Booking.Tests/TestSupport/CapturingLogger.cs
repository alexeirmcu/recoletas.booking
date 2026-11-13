using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.Tests.TestSupport;

public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text, IReadOnlyDictionary<string, object?> Properties, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
            : new Dictionary<string, object?>();
        Entries.Add((logLevel, formatter(state, exception), properties, exception));
    }
}

using Microsoft.Extensions.Logging;

namespace MessageBridge.Infrastructure.Tests.Providers;

internal sealed class ProviderTestLogger<TCategory> : ILogger<TCategory>
{
    public List<string> Messages { get; } = [];
    public List<Dictionary<string, object?>> Scopes { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, string?>> stringEntries)
        {
            Scopes.Add(stringEntries.ToDictionary(entry => entry.Key, entry => (object?)entry.Value));
        }
        else if (state is IEnumerable<KeyValuePair<string, object?>> objectEntries)
        {
            Scopes.Add(objectEntries.ToDictionary(entry => entry.Key, entry => entry.Value));
        }

        return NoopScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var rendered = formatter(state, exception);
        if (!string.IsNullOrWhiteSpace(rendered))
        {
            Messages.Add(rendered);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly IDisposable Instance = new NoopScope();
        public void Dispose()
        {
        }
    }
}

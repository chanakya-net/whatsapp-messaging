using System.Collections.Concurrent;
using System.Reflection;
using ErrorOr;
using MessageBridge.Infrastructure.Messaging;
using Wolverine;

namespace MessageBridge.IntegrationTests.Fixtures;

internal class ScriptedMessageBus : DispatchProxy
{
    private readonly ConcurrentDictionary<string, MessageScript> _scripts = new(StringComparer.Ordinal);

    public void AddScript(string messageId, MessageScript script) => _scripts[messageId] = script;

    public IMessageBus CreateProxy()
    {
        var proxy = Create<IMessageBus, ScriptedMessageBus>();
        var typedProxy = (ScriptedMessageBus)(object)proxy;

        foreach (var item in _scripts)
        {
            typedProxy._scripts[item.Key] = item.Value;
        }

        return proxy;
    }

    public int GetAttemptCount(string messageId) =>
        _scripts.TryGetValue(messageId, out var script) ? script.Attempts.Count : 0;

    public IReadOnlyList<DateTimeOffset> GetAttempts(string messageId) =>
        _scripts.TryGetValue(messageId, out var script) ? script.Attempts.ToArray() : [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "InvokeAsync")
        {
            var message = args![0]!;
            var messageId = (string?)message.GetType().GetProperty("MessageId")?.GetValue(message)
                ?? throw new InvalidOperationException("MessageId is required.");
            return _scripts.TryGetValue(messageId, out var script)
                ? Task.FromResult(script.Next())
                : Task.FromResult<ErrorOr<Success>>(new Success());
        }

        if (targetMethod?.Name == "ToString")
        {
            return nameof(ScriptedMessageBus);
        }

        throw new NotSupportedException($"Unexpected IMessageBus member: {targetMethod?.Name}");
    }
}

internal sealed class MessageScript(Func<ErrorOr<Success>> next)
{
    private readonly Func<ErrorOr<Success>> _next = next;
    public ConcurrentQueue<DateTimeOffset> Attempts { get; } = [];

    public ErrorOr<Success> Next()
    {
        Attempts.Enqueue(DateTimeOffset.UtcNow);
        return _next();
    }

    public static MessageScript FailTimesThenSucceed(int failures, Error error)
    {
        var attempt = 0;
        return new MessageScript(() => ++attempt <= failures ? error : new Success());
    }

    public static MessageScript FailForever(Error error) => new(() => error);
}

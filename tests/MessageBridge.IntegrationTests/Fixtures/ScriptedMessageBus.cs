using System.Collections.Concurrent;
using System.Reflection;
using ErrorOr;
using MessageBridge.Infrastructure.Messaging;
using Wolverine;

namespace MessageBridge.IntegrationTests.Fixtures;

internal class ScriptedMessageBus : DispatchProxy
{
    private ScriptedMessageBusState _state = new();

    public void AddScript(string messageId, MessageScript script) => _state.AddScript(messageId, script);

    public IMessageBus CreateProxy()
    {
        var proxy = Create<IMessageBus, ScriptedMessageBus>();
        var typedProxy = (ScriptedMessageBus)(object)proxy;

        typedProxy._state = _state;

        return proxy;
    }

    public int GetAttemptCount(string messageId) => _state.GetAttemptCount(messageId);

    public IReadOnlyList<DateTimeOffset> GetAttempts(string messageId) => _state.GetAttempts(messageId);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "InvokeAsync")
        {
            var message = args![0]!;
            var messageId = (string?)message.GetType().GetProperty("MessageId")?.GetValue(message)
                ?? throw new InvalidOperationException("MessageId is required.");
            return _state.InvokeAsync(messageId);
        }

        if (targetMethod?.Name == "ToString")
        {
            return nameof(ScriptedMessageBus);
        }

        throw new NotSupportedException($"Unexpected IMessageBus member: {targetMethod?.Name}");
    }
}

internal sealed class ScriptedMessageBusState
{
    private readonly ConcurrentDictionary<string, MessageScript> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    public void AddScript(string messageId, MessageScript script) => _scripts[messageId] = script;

    public int GetAttemptCount(string messageId) =>
        _attempts.TryGetValue(messageId, out var attempts) ? attempts.Count : 0;

    public IReadOnlyList<DateTimeOffset> GetAttempts(string messageId) =>
        _attempts.TryGetValue(messageId, out var attempts) ? attempts.ToArray() : [];

    public Task<ErrorOr<Success>> InvokeAsync(string messageId)
    {
        _attempts.GetOrAdd(messageId, _ => []).Enqueue(DateTimeOffset.UtcNow);
        return _scripts.TryGetValue(messageId, out var script)
            ? script.NextAsync()
            : Task.FromResult<ErrorOr<Success>>(new Success());
    }
}

internal sealed class MessageScript(
    Func<ErrorOr<Success>> next,
    FirstAttemptGate? firstAttemptGate = null)
{
    private readonly Func<ErrorOr<Success>> _next = next;
    private readonly FirstAttemptGate? _firstAttemptGate = firstAttemptGate;
    private int _attemptCount;

    public async Task<ErrorOr<Success>> NextAsync()
    {
        if (Interlocked.Increment(ref _attemptCount) == 1 && _firstAttemptGate is not null)
        {
            _firstAttemptGate.Started.TrySetResult();
            await _firstAttemptGate.Release.Task;
        }

        return _next();
    }

    public Task WaitForFirstAttemptAsync() => _firstAttemptGate?.Started.Task
        ?? throw new InvalidOperationException("The script does not block its first attempt.");

    public void ReleaseFirstAttempt() => _firstAttemptGate?.Release.TrySetResult();

    public static MessageScript FailTimesThenSucceed(
        int failures,
        Error error,
        bool blockFirstAttempt = false)
    {
        var attempt = 0;
        return new MessageScript(
            () => ++attempt <= failures ? error : new Success(),
            FirstAttemptGate.Create(blockFirstAttempt));
    }

    public static MessageScript FailForever(Error error, bool blockFirstAttempt = false) =>
        new(() => error, FirstAttemptGate.Create(blockFirstAttempt));
}

internal sealed class FirstAttemptGate
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static FirstAttemptGate? Create(bool enabled) => enabled ? new FirstAttemptGate() : null;
}

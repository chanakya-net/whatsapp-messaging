using MessageBridge.Publisher.Internal;
using Microsoft.EntityFrameworkCore;

namespace MessageBridge.IntegrationTests.Fixtures;

internal sealed class GatedPublishTransport(
    IMessageBridgePublisherTransport inner,
    bool failFirst) : IMessageBridgePublisherTransport
{
    private readonly TaskCompletionSource _firstFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowPublication =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _successfulPublication =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _publicationFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public async Task PublishAsync(
        MessageBridgePublisherEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        if (failFirst && attempt == 1)
        {
            _firstFailure.TrySetResult();
            throw new InvalidOperationException("Transient integration-test publish failure.");
        }

        await _allowPublication.Task.WaitAsync(cancellationToken);
        try
        {
            await inner.PublishAsync(envelope, cancellationToken);
        }
        catch (Exception exception)
        {
            _publicationFailure.TrySetResult(exception);
            throw;
        }

        _successfulPublication.TrySetResult();
        await _allowCompletion.Task.WaitAsync(cancellationToken);
    }

    public Task WaitForFirstFailureAsync() =>
        _firstFailure.Task.WaitAsync(IntegrationEnvironmentFixture.AssertionTimeout);

    public async Task WaitForSuccessfulPublicationAsync()
    {
        var completed = await Task.WhenAny(
            _successfulPublication.Task,
            _publicationFailure.Task).WaitAsync(IntegrationEnvironmentFixture.AssertionTimeout);
        if (completed == _publicationFailure.Task)
        {
            throw new InvalidOperationException(
                "Real RabbitMQ transport publication failed.",
                await _publicationFailure.Task);
        }
    }

    public void AllowPublication() => _allowPublication.TrySetResult();

    public void AllowDispatcherCompletion() => _allowCompletion.TrySetResult();
}

internal sealed class OutboxTestDbContextFactory(string connectionString)
    : IDbContextFactory<OutboxTestDbContext>
{
    private readonly DbContextOptions<OutboxTestDbContext> _options =
        new DbContextOptionsBuilder<OutboxTestDbContext>()
            .UseNpgsql(connectionString)
            .Options;

    public OutboxTestDbContext CreateDbContext() => new(_options);

    public ValueTask<OutboxTestDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CreateDbContext());
}

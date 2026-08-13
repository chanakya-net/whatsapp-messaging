using System.Collections.Concurrent;
using System.Diagnostics;
using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using MessageBridge.Publisher.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace MessageBridge.Publisher.EntityFrameworkCore.Tests;

[Trait("Category", "Unit")]
public sealed class MessageBridgeOutboxDispatcherBehaviorTests
{
    [Fact]
    public async Task Dispatcher_ProcessesNoMoreThanBatchSizePerPass()
    {
        var (factory, options) = CreateFactory(nameof(Dispatcher_ProcessesNoMoreThanBatchSizePerPass), batchSize: 2);
        await SeedAsync(factory, CreateMessage("1"), CreateMessage("2"), CreateMessage("3"));
        var transport = new FakeTransport();
        var service = BuildDispatcher(factory, transport, options);

        await service.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(transport.TotalAttempts == 2), "Dispatcher did not process one batch.");
        await WaitUntilPublishedCountAsync(factory, 2);
        await service.StopAsync(default);

        transport.TotalAttempts.ShouldBe(2);
        await AssertPublishedCountAsync(factory, 2);
    }

    [Fact]
    public async Task Dispatcher_RespectsConfiguredConcurrency()
    {
        var (factory, options) = CreateFactory(nameof(Dispatcher_RespectsConfiguredConcurrency), batchSize: 3, concurrency: 2);
        await SeedAsync(factory, CreateMessage("1"), CreateMessage("2"), CreateMessage("3"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport((_, _) => gate.Task);
        var service = BuildDispatcher(factory, transport, options);

        await service.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(transport.CurrentlyPublishing == 2), "Dispatcher did not reach concurrency limit.");
        gate.SetResult();
        await WaitUntilAsync(() => Task.FromResult(transport.TotalAttempts == 3), "Dispatcher did not publish all messages.");
        await WaitUntilPublishedCountAsync(factory, 3);
        await service.StopAsync(default);

        transport.MaximumConcurrentPublishes.ShouldBe(2);
    }

    [Fact]
    public async Task Dispatcher_LeavesTerminalFailuresPendingAfterAllRetries()
    {
        var (factory, options) = CreateFactory(
            nameof(Dispatcher_LeavesTerminalFailuresPendingAfterAllRetries),
            maxRetries: 2,
            retryDelayMilliseconds: 1);
        await SeedAsync(factory, CreateMessage("1"));
        var transport = new FakeTransport((_, _) => Task.FromException(new InvalidOperationException("transient")));
        var service = BuildDispatcher(factory, transport, options);

        await service.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(transport.TotalAttempts >= 3), "Dispatcher did not exhaust retries.");
        await service.StopAsync(default);

        transport.TotalAttempts.ShouldBe(3);
        await AssertPublishedCountAsync(factory, 0);
    }

    [Fact]
    public async Task Dispatcher_UsesEmptyHeadersForMalformedAndBlankValues()
    {
        var (factory, options) = CreateFactory(nameof(Dispatcher_UsesEmptyHeadersForMalformedAndBlankValues), batchSize: 2);
        await SeedAsync(
            factory,
            CreateMessage("1", headers: "not-json"),
            CreateMessage("2", headers: " "));
        var transport = new FakeTransport();
        var service = BuildDispatcher(factory, transport, options);

        await service.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(transport.TotalAttempts == 2), "Dispatcher did not publish messages.");
        await WaitUntilPublishedCountAsync(factory, 2);
        await service.StopAsync(default);

        transport.Envelopes.Values.ShouldAllBe(envelope => envelope.Headers.Count == 0);
    }

    [Fact]
    public async Task Dispatcher_StopAsync_CancelsRetryDelayAndLeavesMessagePending()
    {
        var (factory, options) = CreateFactory(
            nameof(Dispatcher_StopAsync_CancelsRetryDelayAndLeavesMessagePending),
            maxRetries: 2,
            retryDelayMilliseconds: 10_000);
        await SeedAsync(factory, CreateMessage("1"));
        var transport = new FakeTransport((_, _) => Task.FromException(new InvalidOperationException("transient")));
        var service = BuildDispatcher(factory, transport, options);

        await service.StartAsync(default);
        await WaitUntilAsync(() => Task.FromResult(transport.TotalAttempts == 1), "Dispatcher did not start first retry.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await service.StopAsync(default).WaitAsync(timeout.Token);

        await AssertPublishedCountAsync(factory, 0);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyOldestPublishedMessagesWithinBatch()
    {
        var options = new MessageBridgeOutboxOptions
        {
            CleanupEnabled = true,
            CleanupRetentionHours = 1,
            CleanupBatchSize = 2,
            CleanupIntervalMilliseconds = 10_000,
        };
        var factory = new TestDbContextFactory(CreateOptions(nameof(Cleanup_RemovesOnlyOldestPublishedMessagesWithinBatch)));
        await SeedAsync(
            factory,
            CreateMessage("oldest", publishedAtUtc: DateTime.UtcNow.AddHours(-4)),
            CreateMessage("next", publishedAtUtc: DateTime.UtcNow.AddHours(-3)),
            CreateMessage("remaining", publishedAtUtc: DateTime.UtcNow.AddHours(-2)),
            CreateMessage("recent", publishedAtUtc: DateTime.UtcNow.AddMinutes(-30)),
            CreateMessage("pending", publishedAtUtc: null));
        var service = new MessageBridgeOutboxCleanupHostedService<TestDbContext>(factory, Options.Create(options));

        await service.StartAsync(default);
        await WaitUntilAsync(async () => !await ExistsAsync(factory, "oldest"), "Cleanup did not remove stale records.");
        await service.StopAsync(default);

        (await ExistsAsync(factory, "next")).ShouldBeFalse();
        (await ExistsAsync(factory, "remaining")).ShouldBeTrue();
        (await ExistsAsync(factory, "recent")).ShouldBeTrue();
        (await ExistsAsync(factory, "pending")).ShouldBeTrue();
    }

    [Fact]
    public async Task Cleanup_Disabled_PreservesStaleMessagesWithoutFixedDelay()
    {
        var options = new MessageBridgeOutboxOptions
        {
            CleanupEnabled = false,
            CleanupIntervalMilliseconds = 10_000,
        };
        var factory = new TestDbContextFactory(CreateOptions(nameof(Cleanup_Disabled_PreservesStaleMessagesWithoutFixedDelay)));
        await SeedAsync(factory, CreateMessage("stale", publishedAtUtc: DateTime.UtcNow.AddDays(-2)));
        var service = new MessageBridgeOutboxCleanupHostedService<TestDbContext>(factory, Options.Create(options));

        await service.StartAsync(default);
        await service.StopAsync(default);

        (await ExistsAsync(factory, "stale")).ShouldBeTrue();
    }

    private static (TestDbContextFactory Factory, MessageBridgeOutboxOptions Options) CreateFactory(
        string name,
        int batchSize = 1,
        int concurrency = 1,
        int maxRetries = 0,
        int retryDelayMilliseconds = 1) =>
        (new TestDbContextFactory(CreateOptions(name)), new MessageBridgeOutboxOptions
        {
            BatchSize = batchSize,
            Concurrency = concurrency,
            MaxRetryAttempts = maxRetries,
            PollIntervalMilliseconds = 10_000,
            RetryDelayMilliseconds = retryDelayMilliseconds,
        });

    private static DbContextOptions<TestDbContext> CreateOptions(string name) =>
        new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(name).Options;

    private static MessageBridgeOutboxDispatcherHostedService<TestDbContext> BuildDispatcher(
        TestDbContextFactory factory,
        FakeTransport transport,
        MessageBridgeOutboxOptions options) =>
        new(factory, transport, Options.Create(options));

    private static MessageBridgeOutboxMessage CreateMessage(
        string id,
        string headers = "{}",
        DateTime? publishedAtUtc = null) => new()
        {
            Id = id,
            MessageId = $"message-{id}",
            CorrelationId = "correlation",
            ExchangeName = "exchange",
            RoutingKey = "routing",
            Headers = headers,
            Payload = [1],
            CreatedAtUtc = DateTime.UtcNow,
            PublishedAtUtc = publishedAtUtc,
        };

    private static async Task SeedAsync(TestDbContextFactory factory, params MessageBridgeOutboxMessage[] messages)
    {
        await using var context = await factory.CreateDbContextAsync();
        context.OutboxMessages.AddRange(messages);
        await context.SaveChangesAsync();
    }

    private static async Task AssertPublishedCountAsync(TestDbContextFactory factory, int expected)
    {
        await using var context = await factory.CreateDbContextAsync();
        (await context.OutboxMessages.CountAsync(message => message.PublishedAtUtc != null)).ShouldBe(expected);
    }

    private static Task WaitUntilPublishedCountAsync(TestDbContextFactory factory, int expected) =>
        WaitUntilAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync();
            return await context.OutboxMessages.CountAsync(message => message.PublishedAtUtc != null) == expected;
        }, "Records were not marked published.");

    private static async Task<bool> ExistsAsync(TestDbContextFactory factory, string id)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await context.OutboxMessages.AnyAsync(message => message.Id == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string message)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Yield();
        }

        throw new XunitException(message);
    }

    private sealed class FakeTransport(Func<MessageBridgePublisherEnvelope, CancellationToken, Task>? publish = null)
        : IMessageBridgePublisherTransport
    {
        private readonly Func<MessageBridgePublisherEnvelope, CancellationToken, Task> _publish =
            publish ?? ((_, _) => Task.CompletedTask);
        private int _currentlyPublishing;
        private int _maximumConcurrentPublishes;
        private int _totalAttempts;

        public int CurrentlyPublishing => Volatile.Read(ref _currentlyPublishing);
        public int MaximumConcurrentPublishes => Volatile.Read(ref _maximumConcurrentPublishes);
        public int TotalAttempts => Volatile.Read(ref _totalAttempts);
        public ConcurrentDictionary<string, MessageBridgePublisherEnvelope> Envelopes { get; } = new();

        public async Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalAttempts);
            Envelopes[envelope.MessageId] = envelope;
            var current = Interlocked.Increment(ref _currentlyPublishing);
            UpdateMaximum(current);
            try
            {
                await _publish(envelope, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _currentlyPublishing);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (current > MaximumConcurrentPublishes)
            {
                if (Interlocked.CompareExchange(ref _maximumConcurrentPublishes, current, MaximumConcurrentPublishes) >= current)
                {
                    return;
                }
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);

        public ValueTask<TestDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            new(CreateDbContext());
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<MessageBridgeOutboxMessage> OutboxMessages { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureMessageBridgeOutbox();
    }
}

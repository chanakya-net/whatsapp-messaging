using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using MessageBridge.Publisher.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Xunit.Sdk;

namespace MessageBridge.Publisher.EntityFrameworkCore.Tests;

public class MessageBridgeOutboxDispatcherTests
{
    [Fact]
    public async Task Dispatcher_PublishesPendingMessagesAndMarksOnlySuccessfulRecords()
    {
        var options = new MessageBridgeOutboxOptions
        {
            PollIntervalMilliseconds = 10,
            BatchSize = 5,
            MaxRetryAttempts = 0,
            Concurrency = 2,
        };
        var transport = new FakeTransport(new Dictionary<string, int> { ["msg-2"] = 1 });
        var contextOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(Dispatcher_PublishesPendingMessagesAndMarksOnlySuccessfulRecords))
            .Options;
        var factory = new TestDbContextFactory(contextOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.OutboxMessages.AddRange(
                new MessageBridgeOutboxMessage
                {
                    Id = "id-1",
                    MessageId = "msg-1",
                    CorrelationId = "corr-1",
                    ExchangeName = "exchange-a",
                    RoutingKey = "routing-a",
                    Headers = """{"content-type":"application/x-protobuf"}""",
                    Payload = new byte[] { 10, 11 },
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new MessageBridgeOutboxMessage
                {
                    Id = "id-2",
                    MessageId = "msg-2",
                    CorrelationId = "corr-2",
                    ExchangeName = "exchange-b",
                    RoutingKey = "routing-b",
                    Headers = "{}",
                    Payload = new byte[] { 20, 21 },
                    CreatedAtUtc = DateTime.UtcNow,
                });
            await context.SaveChangesAsync();
        }

        var service = BuildDispatcher(factory, transport, options);
        await service.StartAsync(default);
        await WaitUntilTransportAttemptedAsync(transport, "msg-1", 1);
        await WaitUntilTransportAttemptedAsync(transport, "msg-2", 1);
        await WaitUntilRecordPublishedAsync(factory, "id-1");
        await service.StopAsync(default);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var published = await verifyContext.OutboxMessages.FirstAsync(x => x.Id == "id-1");
        var failed = await verifyContext.OutboxMessages.FirstAsync(x => x.Id == "id-2");

        published.PublishedAtUtc.ShouldNotBeNull();
        failed.PublishedAtUtc.ShouldBeNull();
        transport.Attempts["msg-1"].ShouldBe(1);
        transport.Attempts["msg-2"].ShouldBe(1);
        transport.Envelopes["msg-1"].ExchangeName.ShouldBe("exchange-a");
        transport.Envelopes["msg-1"].RoutingKey.ShouldBe("routing-a");
        transport.Envelopes["msg-1"].Headers["content-type"].ShouldBe("application/x-protobuf");
    }

    [Fact]
    public async Task Dispatcher_RetriesAndPublishesAfterTransientFailure()
    {
        var options = new MessageBridgeOutboxOptions
        {
            PollIntervalMilliseconds = 10,
            BatchSize = 1,
            MaxRetryAttempts = 2,
            RetryDelayMilliseconds = 1,
            RetryBackoffMultiplier = 2,
            Concurrency = 1,
        };
        var transport = new FakeTransport(new Dictionary<string, int> { ["msg-3"] = 1 });
        var contextOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(Dispatcher_RetriesAndPublishesAfterTransientFailure))
            .Options;
        var factory = new TestDbContextFactory(contextOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.OutboxMessages.Add(new MessageBridgeOutboxMessage
            {
                Id = "id-3",
                MessageId = "msg-3",
                CorrelationId = "corr-3",
                ExchangeName = "exchange-c",
                RoutingKey = "routing-c",
                Headers = "{}",
                Payload = new byte[] { 30 },
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var service = BuildDispatcher(factory, transport, options);
        await service.StartAsync(default);
        await WaitUntilTransportAttemptedAsync(transport, "msg-3", 2);
        await WaitUntilRecordPublishedAsync(factory, "id-3");
        await service.StopAsync(default);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var message = await verifyContext.OutboxMessages.FirstAsync(x => x.Id == "id-3");
        message.PublishedAtUtc.ShouldNotBeNull();
        transport.Attempts["msg-3"].ShouldBe(2);
    }

    [Fact]
    public async Task Cleanup_ServiceDeletesOnlyOldPublishedOutboxRecords()
    {
        var options = new MessageBridgeOutboxOptions
        {
            CleanupEnabled = true,
            CleanupRetentionHours = 24,
            CleanupIntervalMilliseconds = 5,
            CleanupBatchSize = 25,
        };
        var contextOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(Cleanup_ServiceDeletesOnlyOldPublishedOutboxRecords))
            .Options;
        var factory = new TestDbContextFactory(contextOptions);

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.OutboxMessages.AddRange(
                new MessageBridgeOutboxMessage
                {
                    Id = "id-4",
                    MessageId = "msg-4",
                    CorrelationId = "corr-4",
                    ExchangeName = "exchange-d",
                    RoutingKey = "routing-d",
                    Headers = "{}",
                    Payload = new byte[] { 40 },
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
                    PublishedAtUtc = DateTime.UtcNow.AddDays(-2),
                },
                new MessageBridgeOutboxMessage
                {
                    Id = "id-5",
                    MessageId = "msg-5",
                    CorrelationId = "corr-5",
                    ExchangeName = "exchange-e",
                    RoutingKey = "routing-e",
                    Headers = "{}",
                    Payload = new byte[] { 50 },
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                    PublishedAtUtc = DateTime.UtcNow.AddHours(-1),
                },
                new MessageBridgeOutboxMessage
                {
                    Id = "id-6",
                    MessageId = "msg-6",
                    CorrelationId = "corr-6",
                    ExchangeName = "exchange-f",
                    RoutingKey = "routing-f",
                    Headers = "{}",
                    Payload = new byte[] { 60 },
                    CreatedAtUtc = DateTime.UtcNow,
                    PublishedAtUtc = null,
                });
            await context.SaveChangesAsync();
        }

        var cleanupService = new MessageBridgeOutboxCleanupHostedService<TestDbContext>(
            factory,
            Options.Create(options));
        await cleanupService.StartAsync(default);
        await WaitUntilRecordRemovedAsync(factory, "id-4");
        await cleanupService.StopAsync(default);

        await using var verifyContext = await factory.CreateDbContextAsync();
        verifyContext.OutboxMessages.Any(x => x.Id == "id-4").ShouldBeFalse();
        verifyContext.OutboxMessages.Any(x => x.Id == "id-5").ShouldBeTrue();
        verifyContext.OutboxMessages.Any(x => x.Id == "id-6").ShouldBeTrue();
    }

    [Fact]
    public async Task Cleanup_Service_DoesNotDelete_WhenCleanupDisabled()
    {
        var options = new MessageBridgeOutboxOptions
        {
            CleanupEnabled = false,
            CleanupIntervalMilliseconds = 5,
            CleanupBatchSize = 25,
        };
        var contextOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(Cleanup_Service_DoesNotDelete_WhenCleanupDisabled))
            .Options;
        var factory = new TestDbContextFactory(contextOptions);

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.OutboxMessages.Add(new MessageBridgeOutboxMessage
            {
                Id = "id-7",
                MessageId = "msg-7",
                CorrelationId = "corr-7",
                ExchangeName = "exchange-g",
                RoutingKey = "routing-g",
                Headers = "{}",
                Payload = new byte[] { 70 },
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
                PublishedAtUtc = DateTime.UtcNow.AddDays(-2),
            });
            await context.SaveChangesAsync();
        }

        var cleanupService = new MessageBridgeOutboxCleanupHostedService<TestDbContext>(
            factory,
            Options.Create(options));
        await cleanupService.StartAsync(default);
        await Task.Delay(50);
        await cleanupService.StopAsync(default);

        await using var verifyContext = await factory.CreateDbContextAsync();
        verifyContext.OutboxMessages.Any(x => x.Id == "id-7").ShouldBeTrue();
    }

    private static MessageBridgeOutboxDispatcherHostedService<TestDbContext> BuildDispatcher(
        TestDbContextFactory contextFactory,
        IMessageBridgePublisherTransport transport,
        MessageBridgeOutboxOptions options)
    {
        return new MessageBridgeOutboxDispatcherHostedService<TestDbContext>(
            contextFactory,
            transport,
            Options.Create(options));
    }

    private sealed class FakeTransport : IMessageBridgePublisherTransport
    {
        private readonly IDictionary<string, int> _failures;
        private readonly ConcurrentDictionary<string, int> _attempts = new();

        public FakeTransport(IDictionary<string, int> failures)
        {
            _failures = failures;
        }

        public IReadOnlyDictionary<string, int> Attempts => _attempts;
        public ConcurrentDictionary<string, MessageBridgePublisherEnvelope> Envelopes { get; } = new();

        public bool HasAttempt(string messageId, int count) =>
            _attempts.TryGetValue(messageId, out var attempts) && attempts >= count;

        public Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken)
        {
            var attempt = _attempts.AddOrUpdate(envelope.MessageId, 1, (_, count) => count + 1);
            Envelopes[envelope.MessageId] = envelope;

            if (_failures.TryGetValue(envelope.MessageId, out var failureCount) && attempt <= failureCount)
            {
                throw new InvalidOperationException("Transport simulation failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<TestDbContext>
    {
        private readonly DbContextOptions<TestDbContext> _options;

        public TestDbContextFactory(DbContextOptions<TestDbContext> options)
        {
            _options = options;
        }

        public TestDbContext CreateDbContext() => new TestDbContext(_options);

        public ValueTask<TestDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            new(CreateDbContext());
    }

    private sealed class TestDbContext : DbContext
    {
        public DbSet<MessageBridgeOutboxMessage> OutboxMessages { get; set; } = null!;

        public TestDbContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMessageBridgeOutbox();
        }
    }

    private static async Task WaitUntilTransportAttemptedAsync(
        FakeTransport transport,
        string messageId,
        int attemptCount)
    {
        await WaitUntilAsync(
            () => Task.FromResult(transport.HasAttempt(messageId, attemptCount)),
            TimeSpan.FromSeconds(1),
            $"Transport did not attempt message '{messageId}' {attemptCount} times.");
    }

    private static async Task WaitUntilRecordPublishedAsync(TestDbContextFactory factory, string id)
    {
        await WaitUntilAsync(
            async () =>
            {
                await using var context = await factory.CreateDbContextAsync();
                return await context.OutboxMessages.AnyAsync(x => x.Id == id && x.PublishedAtUtc != null);
            },
            TimeSpan.FromSeconds(1),
            $"Outbox message '{id}' was not marked published in time.");
    }

    private static async Task WaitUntilRecordRemovedAsync(
        TestDbContextFactory factory,
        string recordId)
    {
        await WaitUntilAsync(
            async () =>
            {
                await using var context = await factory.CreateDbContextAsync();
                return !await context.OutboxMessages.AnyAsync(x => x.Id == recordId);
            },
            TimeSpan.FromSeconds(1),
            $"Outbox record '{recordId}' was not removed in time.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new XunitException(message);
    }
}

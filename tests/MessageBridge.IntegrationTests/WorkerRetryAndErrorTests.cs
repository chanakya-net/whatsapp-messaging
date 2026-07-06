using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ErrorOr;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;

namespace MessageBridge.IntegrationTests;

public sealed class WorkerRetryAndErrorTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbitMq;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .Build();

        _rabbitMq = new RabbitMqBuilder()
            .WithImage("masstransit/rabbitmq:3.13")
            .Build();

        await _postgres.StartAsync();
        await _rabbitMq.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task Validation_failures_are_marked_rejected_without_retry_or_error_queue()
    {
        var bus = new ScriptedMessageBus();
        await using var harness = await StartHarnessAsync(bus, [200.Milliseconds(), 400.Milliseconds(), 800.Milliseconds()]);

        var messageId = $"email-{Guid.NewGuid():N}";
        await harness.PublishAsync(new SendEmailConfirmationCommand
        {
            MessageId = messageId,
            TenantId = "tenant-1",
            RecipientEmail = "not-an-email",
            RecipientName = "Alex",
            ConfirmationToken = "https://example.com/token",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        });

        var record = await harness.WaitForRecordAsync(messageId, nameof(SendEmailConfirmationCommand), ProcessingStatus.Rejected);

        bus.GetAttemptCount(messageId).ShouldBe(0);
        record.FailureReason.ShouldNotBeNull();
        record.FailureReason.ShouldContain("Validation.RecipientEmail");
        record.FailureReason.ShouldContain("Validation.ConfirmationToken");
        (await harness.GetQueueDepthAsync("send-email-confirmation_error")).ShouldBe(0);
    }

    [Fact]
    public async Task Transient_failures_use_three_immediate_retries_before_success()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        bus.AddScript(
            messageId,
            MessageScript.FailTimesThenSucceed(
                3,
                Error.Failure("Provider.Send", "temporary outage token=secret-value")));

        await using var harness = await StartHarnessAsync(bus, [1.Seconds(), 2.Seconds(), 3.Seconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        var attempts = bus.GetAttempts(messageId);

        attempts.Count.ShouldBe(4);
        record.Status.ShouldBe(ProcessingStatus.Completed);
        (attempts[3] - attempts[0]).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await harness.GetQueueDepthAsync("send-whats-app-message_error")).ShouldBe(0);
    }

    [Fact]
    public async Task Transient_failures_are_redelivered_on_configured_schedule()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        bus.AddScript(
            messageId,
            MessageScript.FailTimesThenSucceed(
                4,
                Error.Failure("Provider.Send", "temporary outage token=secret-value")));

        await using var harness = await StartHarnessAsync(bus, [1.Seconds(), 2.Seconds(), 3.Seconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        var sawFailedBeforeRecovery = await harness.ObservedStatusWithinAsync(
            messageId,
            nameof(SendWhatsAppMessageCommand),
            ProcessingStatus.Failed,
            900.Milliseconds());
        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        var attempts = bus.GetAttempts(messageId);

        sawFailedBeforeRecovery.ShouldBeFalse();
        attempts.Count.ShouldBeGreaterThanOrEqualTo(5);
        (attempts[4] - attempts[3]).ShouldBeGreaterThanOrEqualTo(900.Milliseconds());
        record.Status.ShouldBe(ProcessingStatus.Completed);
        (await harness.GetQueueDepthAsync("send-whats-app-message_error")).ShouldBe(0);
    }

    [Fact]
    public async Task Exhausted_failures_are_persisted_and_moved_to_error_queue()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        bus.AddScript(
            messageId,
            MessageScript.FailForever(
                Error.Failure(
                    "Provider.Send",
                    "temporary outage token=super-secret-token phone=+1 (415) 555-2671")));

        await using var harness = await StartHarnessAsync(bus, [150.Milliseconds(), 300.Milliseconds(), 450.Milliseconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Failed);
        record.FailureReason.ShouldNotBeNull();
        record.FailureReason.ShouldContain("*******2671");
        record.FailureReason.ShouldNotContain("super-secret-token");

        await harness.WaitForQueueDepthAsync("send-whats-app-message_error", 1);
        await Task.Delay(500);
        (await harness.GetQueueDepthAsync("send-whats-app-message_error")).ShouldBe(1);
        bus.GetAttemptCount(messageId).ShouldBeGreaterThan(4);
    }

    private async Task<TestHarness> StartHarnessAsync(
        ScriptedMessageBus bus,
        IReadOnlyList<TimeSpan> delayedIntervals)
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = $"messagebridge_it_{Guid.NewGuid():N}"
        };

        var environmentPrefix = $"it-{Guid.NewGuid():N}";
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionStringBuilder.ConnectionString,
            ["RabbitMq:ConnectionString"] = _rabbitMq!.GetConnectionString(),
            ["MessageBridge:Topology:EnvironmentPrefix"] = environmentPrefix,
            ["MessageBridge:TransportRetry:ImmediateRetryCount"] = "3",
        };

        for (var index = 0; index < delayedIntervals.Count; index++)
        {
            settings[$"MessageBridge:TransportRetry:DelayedRedeliveryIntervals:{index}"] =
                delayedIntervals[index].ToString("c");
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOptions<MassTransitHostOptions>()
            .Configure(options =>
            {
                options.WaitUntilStarted = true;
                options.StartTimeout = TimeSpan.FromSeconds(30);
                options.StopTimeout = TimeSpan.FromSeconds(30);
            });
        builder.Services.AddDbContext<MessageBridgeDbContext>(options =>
            options.UseNpgsql(connectionStringBuilder.ConnectionString));
        builder.Services.AddScoped<MessageBridge.Application.Persistence.IMessageProcessingStore, MessageProcessingStore>();
        builder.Services.AddSingleton<IMessageBus>(bus.CreateProxy());
        builder.Services.AddMessageBridgeMassTransit(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageBridgeDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestHarness(host, connectionStringBuilder.ConnectionString, _rabbitMq!, environmentPrefix);
    }

    private static SendWhatsAppMessageCommand CreateWhatsAppCommand(string messageId)
    {
        return new SendWhatsAppMessageCommand
        {
            MessageId = messageId,
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Ada" },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
    }

    private sealed class TestHarness(
        IHost host,
        string connectionString,
        RabbitMqContainer rabbitMq,
        string environmentPrefix) : IAsyncDisposable
    {
        private readonly IHost _host = host;
        private readonly string _connectionString = connectionString;
        private readonly RabbitMqContainer _rabbitMq = rabbitMq;
        private readonly string _environmentPrefix = environmentPrefix;

        public async Task PublishAsync<TMessage>(TMessage message)
            where TMessage : class
        {
            await _host.Services.GetRequiredService<IPublishEndpoint>().Publish(message);
        }

        public async Task<MessageProcessingRecord> WaitForRecordAsync(
            string messageId,
            string messageType,
            ProcessingStatus expectedStatus)
        {
            MessageProcessingRecord? lastSeen = null;

            try
            {
                return await WaitAsync(async () =>
                {
                    await using var dbContext = CreateDbContext();
                    lastSeen = await dbContext.MessageProcessingRecords.SingleOrDefaultAsync(
                        item => item.MessageId == messageId
                            && item.MessageType == messageType);

                    return lastSeen?.Status == expectedStatus ? lastSeen : null;
                });
            }
            catch (TimeoutException exception)
            {
                var details = lastSeen is null
                    ? "no record was persisted"
                    : $"last status={lastSeen.Status}, failure='{lastSeen.FailureReason}', processed_at={lastSeen.ProcessedAt:o}";

                throw new TimeoutException(
                    $"Expected {messageType}/{messageId} to reach {expectedStatus}; {details}.",
                    exception);
            }
        }

        public async Task<int> GetQueueDepthAsync(string queueSuffix)
        {
            var (depth, _) = await GetQueueDepthWithListingAsync(queueSuffix);
            return depth;
        }

        public async Task<bool> ObservedStatusWithinAsync(
            string messageId,
            string messageType,
            ProcessingStatus expectedStatus,
            TimeSpan duration)
        {
            var timeoutAt = DateTimeOffset.UtcNow.Add(duration);

            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                await using var dbContext = CreateDbContext();
                var status = await dbContext.MessageProcessingRecords
                    .Where(item => item.MessageId == messageId && item.MessageType == messageType)
                    .Select(item => (ProcessingStatus?)item.Status)
                    .SingleOrDefaultAsync();

                if (status == expectedStatus)
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return false;
        }

        private async Task<(int Depth, string Listing)> GetQueueDepthWithListingAsync(string queueSuffix)
        {
            var result = await _rabbitMq.ExecAsync(["rabbitmqctl", "list_queues", "name", "messages"]);
            result.ExitCode.ShouldBe(0);

            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (columns.Length != 2)
                {
                    continue;
                }

                if (columns[0].EndsWith(queueSuffix, StringComparison.Ordinal))
                {
                    return (int.TryParse(columns[1], out var count) ? count : 0, result.Stdout);
                }
            }

            return (0, result.Stdout);
        }

        public async Task WaitForQueueDepthAsync(string queueSuffix, int minimumDepth)
        {
            string? lastQueues = null;

            try
            {
                await WaitUntilAsync(async () =>
                {
                    var (depth, queues) = await GetQueueDepthWithListingAsync(queueSuffix);
                    lastQueues = queues;
                    return depth >= minimumDepth;
                });
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Expected queue ending with '{queueSuffix}' to reach depth {minimumDepth}. Queues:{Environment.NewLine}{lastQueues}",
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        private MessageBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new MessageBridgeDbContext(options);
        }

        private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
            where T : class
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                var value = await probe();
                if (value is not null)
                {
                    return value;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException("Expected condition was not met before timeout.");
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> probe)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                if (await probe())
                {
                    return;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException("Expected condition was not met before timeout.");
        }
    }

    private class ScriptedMessageBus : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, MessageScript> _scripts = new(StringComparer.Ordinal);

        public void AddScript(string messageId, MessageScript script)
        {
            _scripts[messageId] = script;
        }

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

        public int GetAttemptCount(string messageId)
        {
            return _scripts.TryGetValue(messageId, out var script) ? script.Attempts.Count : 0;
        }

        public IReadOnlyList<DateTimeOffset> GetAttempts(string messageId)
        {
            return _scripts.TryGetValue(messageId, out var script)
                ? script.Attempts.ToArray()
                : [];
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "InvokeAsync")
            {
                var message = args![0]!;
                var messageId = (string?)message.GetType().GetProperty("MessageId")?.GetValue(message)
                    ?? throw new InvalidOperationException("MessageId is required.");

                if (!_scripts.TryGetValue(messageId, out var script))
                {
                    return Task.FromResult<ErrorOr<Success>>(new Success());
                }

                return Task.FromResult(script.Next());
            }

            if (targetMethod?.Name == "ToString")
            {
                return nameof(ScriptedMessageBus);
            }

            throw new NotSupportedException($"Unexpected IMessageBus member: {targetMethod?.Name}");
        }
    }

    private sealed class MessageScript(Func<ErrorOr<Success>> next)
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

        public static MessageScript FailForever(Error error)
        {
            return new MessageScript(() => error);
        }
    }
}

internal static class TimeSpanIntExtensions
{
    public static TimeSpan Milliseconds(this int value) => TimeSpan.FromMilliseconds(value);

    public static TimeSpan Seconds(this int value) => TimeSpan.FromSeconds(value);
}

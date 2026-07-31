using ErrorOr;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerRetryAndErrorTests(IntegrationEnvironmentFixture fixture)
{
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
        record.FailureReason.ShouldNotContain("https://example.com/token");
        await harness.AssertQueueDepthRemainsAsync("send-email-confirmation_error", 0, 1.Seconds());
    }

    [Fact]
    public async Task Scripted_message_bus_counts_unscripted_provider_invocations()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();

        await bus.CreateProxy().InvokeAsync<ErrorOr<Success>>(CreateWhatsAppCommand(messageId));

        bus.GetAttemptCount(messageId).ShouldBe(1);
    }

    [Fact]
    public async Task Transient_failures_use_three_immediate_retries_before_success()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        var script = MessageScript.FailTimesThenSucceed(
            3,
            Error.Failure("Provider.Send", "temporary outage token=secret-value"),
            blockFirstAttempt: true);
        bus.AddScript(messageId, script);

        await using var harness = await StartHarnessAsync(bus, [1.Seconds(), 2.Seconds(), 3.Seconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        await script.WaitForFirstAttemptAsync();
        await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Processing);
        script.ReleaseFirstAttempt();
        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        var attempts = bus.GetAttempts(messageId);

        attempts.Count.ShouldBe(4);
        record.Status.ShouldBe(ProcessingStatus.Completed);
        (attempts[3] - attempts[0]).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await harness.AssertQueueDepthRemainsAsync("send-whats-app-message_error", 0, 1.Seconds());
    }

    [Fact]
    public async Task Transient_failures_are_redelivered_on_configured_schedule()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        var script = MessageScript.FailTimesThenSucceed(
            4,
            Error.Failure("Provider.Send", "temporary outage token=secret-value"),
            blockFirstAttempt: true);
        bus.AddScript(messageId, script);

        await using var harness = await StartHarnessAsync(bus, [1.Seconds(), 2.Seconds(), 3.Seconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        await script.WaitForFirstAttemptAsync();
        await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Processing);
        script.ReleaseFirstAttempt();
        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        var attempts = bus.GetAttempts(messageId);

        attempts.Count.ShouldBe(5);
        (attempts[4] - attempts[3]).ShouldBeGreaterThanOrEqualTo(900.Milliseconds());
        (attempts[4] - attempts[3]).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        record.Status.ShouldBe(ProcessingStatus.Completed);
        await harness.AssertQueueDepthRemainsAsync("send-whats-app-message_error", 0, 1.Seconds());
    }

    [Fact]
    public async Task Exhausted_failures_are_persisted_and_moved_to_error_queue()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        var script = MessageScript.FailForever(
            Error.Failure(
                "Provider.Send",
                "temporary outage token=super-secret-token phone=+1 (415) 555-2671 " +
                "connection_string=Host=database;Username=admin;Password=unsafe-password " +
                "payload={\"recipient\":\"+14155552671\",\"body\":\"private payload\"}"),
            blockFirstAttempt: true);
        bus.AddScript(messageId, script);

        await using var harness = await StartHarnessAsync(bus, [150.Milliseconds(), 300.Milliseconds(), 450.Milliseconds()]);
        await harness.PublishAsync(CreateWhatsAppCommand(messageId));

        await script.WaitForFirstAttemptAsync();
        await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Processing);
        script.ReleaseFirstAttempt();
        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Failed);
        record.FailureReason.ShouldNotBeNull();
        record.FailureReason.ShouldContain("*******2671");
        record.FailureReason.ShouldNotContain("super-secret-token");
        record.FailureReason.ShouldNotContain("Host=database");
        record.FailureReason.ShouldNotContain("admin");
        record.FailureReason.ShouldNotContain("unsafe-password");
        record.FailureReason.ShouldNotContain("private payload");
        record.FailureReason.ShouldNotContain("+14155552671");

        await harness.WaitForQueueDepthAsync("send-whats-app-message_error", 1);
        await harness.AssertQueueDepthRemainsAsync("send-whats-app-message_error", 1, 1.Seconds());
        bus.GetAttemptCount(messageId).ShouldBe(16);
    }

    private async Task<TestHarness> StartHarnessAsync(
        ScriptedMessageBus bus,
        IReadOnlyList<TimeSpan> delayedIntervals)
    {
        var database = await MigratedDatabaseScenario.CreateAsync(fixture);
        var connectionString = database.DbContext.Database.GetConnectionString()!;

        var environmentPrefix = IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["RabbitMq:ConnectionString"] = fixture.GetRabbitMqConnectionString(),
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
        builder.Services.AddSingleton<IMessageBus>(bus.CreateProxy());
        builder.Services.AddMessageBridgeMassTransit(builder.Configuration);

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            return new TestHarness(host, database, fixture, environmentPrefix);
        }
        catch
        {
            host.Dispose();
            await database.DisposeAsync();
            throw;
        }
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
        MigratedDatabaseScenario database,
        IntegrationEnvironmentFixture fixture,
        string environmentPrefix) : IAsyncDisposable
    {
        private readonly IHost _host = host;
        private readonly MigratedDatabaseScenario _database = database;
        private readonly IntegrationEnvironmentFixture _fixture = fixture;
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
            return await _fixture.GetQueueDepthAsync(GetQueueName(queueSuffix));
        }

        public Task AssertQueueDepthRemainsAsync(
            string queueSuffix,
            int expectedDepth,
            TimeSpan duration)
        {
            return IntegrationEnvironmentFixture.AssertRemainsAsync(
                async () => await GetQueueDepthAsync(queueSuffix) == expectedDepth,
                duration,
                $"Queue {GetQueueName(queueSuffix)} changed from depth {expectedDepth}.");
        }

        public async Task WaitForQueueDepthAsync(string queueSuffix, int minimumDepth)
        {
            try
            {
                await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
                    async () => await GetQueueDepthAsync(queueSuffix) >= minimumDepth ? queueSuffix : null,
                    $"Expected queue {GetQueueName(queueSuffix)} to reach depth {minimumDepth}.");
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Expected queue {GetQueueName(queueSuffix)} to reach depth {minimumDepth}.",
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _database.DisposeAsync();
        }

        private MessageBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
                .UseNpgsql(_database.DbContext.Database.GetConnectionString())
                .Options;
            return new MessageBridgeDbContext(options);
        }

        private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
            where T : class
        {
            return await IntegrationEnvironmentFixture.PollUntilAssertedAsync(probe);
        }

        private string GetQueueName(string queueSuffix)
        {
            return $"{_environmentPrefix}-{queueSuffix}";
        }
    }

}

internal static class TimeSpanIntExtensions
{
    public static TimeSpan Milliseconds(this int value) => TimeSpan.FromMilliseconds(value);

    public static TimeSpan Seconds(this int value) => TimeSpan.FromSeconds(value);
}

using ErrorOr;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
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
public sealed class WorkerIdempotencyTests(IntegrationEnvironmentFixture fixture)
{
    [Fact]
    public async Task Duplicate_whatsapp_delivery_invokes_provider_once()
    {
        var bus = new ScriptedMessageBus();
        await using var harness = await StartHarnessAsync(bus);

        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var command = CreateWhatsAppCommand(messageId);

        await harness.PublishAsync(command);
        await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        await harness.PublishAsync(command);

        await harness.AssertQueueDepthRemainsAsync("send-whats-app-message_error", 0, TimeSpan.FromSeconds(1));
        bus.GetAttemptCount(messageId).ShouldBe(1);

        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
        record.Status.ShouldBe(ProcessingStatus.Completed);
        await harness.AssertSingleRecordAsync(messageId, nameof(SendWhatsAppMessageCommand));
    }

    [Fact]
    public async Task Duplicate_email_delivery_invokes_provider_once()
    {
        var bus = new ScriptedMessageBus();
        await using var harness = await StartHarnessAsync(bus);

        var messageId = $"email-{Guid.NewGuid():N}";
        var command = CreateEmailCommand(messageId);

        await harness.PublishAsync(command);
        await harness.WaitForRecordAsync(messageId, nameof(SendEmailConfirmationCommand), ProcessingStatus.Completed);
        await harness.PublishAsync(command);

        await harness.AssertQueueDepthRemainsAsync("send-email-confirmation_error", 0, TimeSpan.FromSeconds(1));
        bus.GetAttemptCount(messageId).ShouldBe(1);

        var record = await harness.WaitForRecordAsync(messageId, nameof(SendEmailConfirmationCommand), ProcessingStatus.Completed);
        record.Status.ShouldBe(ProcessingStatus.Completed);
        await harness.AssertSingleRecordAsync(messageId, nameof(SendEmailConfirmationCommand));
    }

    [Fact]
    public async Task Concurrent_duplicate_deliveries_invoke_provider_once()
    {
        var messageId = $"whatsapp-{Guid.NewGuid():N}";
        var bus = new ScriptedMessageBus();
        var script = MessageScript.FailTimesThenSucceed(0, Error.Failure("Provider.Send", "unused"), blockFirstAttempt: true);
        bus.AddScript(messageId, script);

        await using var harness = await StartHarnessAsync(bus);
        var command = CreateWhatsAppCommand(messageId);

        await harness.PublishAsync(command);
        await script.WaitForFirstAttemptAsync();
        await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Processing);

        await harness.PublishAsync(command);
        await harness.PublishAsync(command);

        script.ReleaseFirstAttempt();
        var record = await harness.WaitForRecordAsync(messageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);

        record.Status.ShouldBe(ProcessingStatus.Completed);
        bus.GetAttemptCount(messageId).ShouldBe(1);
        await harness.AssertSingleRecordAsync(messageId, nameof(SendWhatsAppMessageCommand));
        await harness.AssertQueueDepthRemainsAsync("send-whats-app-message_error", 0, TimeSpan.FromSeconds(1));
    }

    private async Task<TestHarness> StartHarnessAsync(ScriptedMessageBus bus)
    {
        var database = await MigratedDatabaseScenario.CreateAsync(fixture);
        var connectionString = database.DbContext.Database.GetConnectionString()!;
        var environmentPrefix = IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["RabbitMq:ConnectionString"] = fixture.GetRabbitMqConnectionString(),
            ["MessageBridge:Topology:EnvironmentPrefix"] = environmentPrefix,
            ["MessageBridge:TransportRetry:ImmediateRetryCount"] = "0",
        };

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

    private static SendWhatsAppMessageCommand CreateWhatsAppCommand(string messageId) =>
        new()
        {
            MessageId = messageId,
            TenantId = "tenant-int",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Alice" },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

    private static SendEmailConfirmationCommand CreateEmailCommand(string messageId) =>
        new()
        {
            MessageId = messageId,
            TenantId = "tenant-int",
            RecipientEmail = "user@example.com",
            RecipientName = "Alice",
            ConfirmationToken = "token-abc",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

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
                return await IntegrationEnvironmentFixture.PollUntilAssertedAsync(async () =>
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

        public async Task AssertSingleRecordAsync(string messageId, string messageType)
        {
            await using var dbContext = CreateDbContext();
            var count = await dbContext.MessageProcessingRecords.CountAsync(
                item => item.MessageId == messageId && item.MessageType == messageType);
            count.ShouldBe(1);
        }

        public async Task<int> GetQueueDepthAsync(string queueSuffix)
        {
            return await _fixture.GetQueueDepthAsync(GetQueueName(queueSuffix));
        }

        public Task AssertQueueDepthRemainsAsync(string queueSuffix, int expectedDepth, TimeSpan duration)
        {
            return IntegrationEnvironmentFixture.AssertRemainsAsync(
                async () => await GetQueueDepthAsync(queueSuffix) == expectedDepth,
                duration,
                $"Queue {GetQueueName(queueSuffix)} changed from depth {expectedDepth}.");
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

        private string GetQueueName(string queueSuffix) => $"{_environmentPrefix}-{queueSuffix}";
    }
}

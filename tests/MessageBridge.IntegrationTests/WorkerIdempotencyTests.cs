using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using Google.Protobuf;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.Infrastructure.Messaging.Processing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using System.Collections.Concurrent;

namespace MessageBridge.IntegrationTests;

internal sealed class ProviderInvokeCounter
{
    private readonly ConcurrentDictionary<string, int> _counts = [];

    public async Task InvokeAsync(string messageId)
    {
        _counts.AddOrUpdate(messageId, 1, (_, count) => count + 1);
        await Task.CompletedTask;
    }

    public int GetCount(string messageId) => _counts.TryGetValue(messageId, out var count) ? count : 0;
}

public sealed class WorkerIdempotencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("MESSAGEBRIDGE_TEST_DATABASE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(_connectionString))
            return;

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (!_container.State.Equals(default))
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task First_valid_message_records_lifecycle_and_payload_hash()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var coordinator = new MessageProcessingCoordinator(store);

        var command = CreateWhatsAppCommand("wamid.int.1");
        var metadata = new Dictionary<string, string?> { ["tenantId"] = "tenant-int" };
        var statuses = new List<ProcessingStatus>();
        var payloadHash = ComputePayloadHash(command);

        await coordinator.ProcessAsync(
            command.MessageId,
            "SendWhatsAppMessage",
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ =>
            {
                var snapshot = await store.GetAsync(command.MessageId, "SendWhatsAppMessage");
                snapshot.ShouldNotBeNull();
                statuses.Add(snapshot.Status);
                await Task.CompletedTask;
            },
            CancellationToken.None);

        var record = await store.GetAsync(command.MessageId, "SendWhatsAppMessage");
        record.ShouldNotBeNull();
        statuses.ShouldContain(ProcessingStatus.Processing);
        record.Status.ShouldBe(ProcessingStatus.Completed);
        record.PayloadHash.ShouldBe(payloadHash);
        record.ProviderMetadata.ShouldContainKey("tenantId");
        record.ProviderMetadata["tenantId"].ShouldBe("tenant-int");
        record.ProviderMetadata.ShouldHaveSingleItem();
        statuses.Contains(ProcessingStatus.Received).ShouldBeFalse();
    }

    [Fact]
    public async Task Duplicate_succeeded_message_is_skipped_without_provider_execution()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var coordinator = new MessageProcessingCoordinator(store);

        var command = CreateEmailCommand("wamid.int.2");
        var metadata = new Dictionary<string, string?> { ["tenantId"] = "tenant-int" };
        var payloadHash = ComputePayloadHash(command);

        var invokedCount = 0;
        await coordinator.ProcessAsync(
            command.MessageId,
            "SendEmailConfirmation",
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ =>
            {
                invokedCount++;
                await Task.CompletedTask;
            },
            CancellationToken.None);

        var secondResult = await coordinator.ProcessAsync(
            command.MessageId,
            "SendEmailConfirmation",
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ =>
            {
                invokedCount++;
                await Task.CompletedTask;
            },
            CancellationToken.None);

        invokedCount.ShouldBe(1);
        secondResult.ShouldBeFalse();

        var record = await store.GetAsync(command.MessageId, "SendEmailConfirmation");
        record.ShouldNotBeNull();
        record.Status.ShouldBe(ProcessingStatus.Completed);
        record.PayloadHash.ShouldBe(payloadHash);
        record.ProviderMetadata.ShouldContainKey("tenantId");
    }

    [Fact]
    public async Task Concurrent_duplicate_deliveries_invoke_provider_once()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var coordinator = new MessageProcessingCoordinator(store);

        var command = CreateWhatsAppCommand("wamid.int.concurrent1");
        var messageType = "SendWhatsAppMessage";
        var metadata = new Dictionary<string, string?> { ["tenantId"] = "tenant-int" };
        var payloadHash = ComputePayloadHash(command);

        var invokeCounter = new ProviderInvokeCounter();

        var result1 = await coordinator.ProcessAsync(
            command.MessageId,
            messageType,
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ =>
            {
                await Task.Delay(50);
                await invokeCounter.InvokeAsync(command.MessageId);
            },
            CancellationToken.None);

        var result2 = await coordinator.ProcessAsync(
            command.MessageId,
            messageType,
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ => await invokeCounter.InvokeAsync(command.MessageId),
            CancellationToken.None);

        var result3 = await coordinator.ProcessAsync(
            command.MessageId,
            messageType,
            payloadHash,
            "MessageBridge.Worker",
            metadata,
            async _ => await invokeCounter.InvokeAsync(command.MessageId),
            CancellationToken.None);

        invokeCounter.GetCount(command.MessageId).ShouldBe(1);
        result1.ShouldBe(true);
        result2.ShouldBe(false);
        result3.ShouldBe(false);

        var record = await store.GetAsync(command.MessageId, messageType);
        record.ShouldNotBeNull();
        record.Status.ShouldBe(ProcessingStatus.Completed);
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
            CorrelationId = " ",
            RequestedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

    private static SendEmailConfirmationCommand CreateEmailCommand(string messageId) =>
        new()
        {
            MessageId = messageId,
            TenantId = "tenant-int",
            RecipientEmail = "user@example.com",
            RecipientName = "Alice",
            ConfirmationToken = "token-abc",
            ExpiresAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

    private static string ComputePayloadHash(SendWhatsAppMessageCommand command)
    {
        var bytes = command.ToByteArray();
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputePayloadHash(SendEmailConfirmationCommand command)
    {
        var bytes = command.ToByteArray();
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<MessageBridgeDbContext> CreateDbContextAsync()
    {
        var baseConnectionString = _connectionString
            ?? throw new InvalidOperationException("Test database connection string not configured.");
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = $"messagebridge_tests_{Guid.NewGuid():N}"
        };

        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionString.ConnectionString)
            .Options;

        var dbContext = new MessageBridgeDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return dbContext;
    }
}

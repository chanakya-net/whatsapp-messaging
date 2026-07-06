using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MessageBridge.Infrastructure.Tests.Persistence;

public sealed class MessageProcessingStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _sharedConnectionString;

    public async Task InitializeAsync()
    {
        _sharedConnectionString = Environment.GetEnvironmentVariable("MESSAGEBRIDGE_TEST_DATABASE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(_sharedConnectionString))
        {
            return;
        }

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_persists_and_is_retrievable()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var request = new CreateMessageProcessingRequest(
            "wamid.create-1",
            "inbound.whatsapp",
            "payload-hash-1",
            "meta",
            new Dictionary<string, string?> { ["providerMessageId"] = "provider-1" });

        var result = await store.CreateAsync(request);
        var stored = await store.GetAsync(request.MessageId, request.MessageType);

        Assert.Equal(CreateMessageProcessingOutcome.Created, result.Outcome);
        Assert.NotNull(stored);
        Assert.Equal(request.MessageId, stored!.MessageId);
        Assert.Equal(request.MessageType, stored.MessageType);
        Assert.Equal(ProcessingStatus.Received, stored.Status);
        Assert.Equal(request.PayloadHash, stored.PayloadHash);
        Assert.Equal(request.Provider, stored.Provider);
        Assert.Equal("provider-1", stored.ProviderMetadata["providerMessageId"]);
        Assert.Equal(stored.Id, result.Record.Id);
    }

    [Fact]
    public async Task CreateAsync_duplicate_message_returns_existing()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var request = new CreateMessageProcessingRequest(
            "wamid.duplicate-1",
            "inbound.whatsapp",
            "payload-hash-2",
            "meta",
            new Dictionary<string, string?> { ["providerMessageId"] = "provider-2" });

        var first = await store.CreateAsync(request);
        var second = await store.CreateAsync(request);
        var storedCount = await dbContext.MessageProcessingRecords.CountAsync();

        Assert.Equal(CreateMessageProcessingOutcome.Created, first.Outcome);
        Assert.Equal(CreateMessageProcessingOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, storedCount);
    }

    [Fact]
    public async Task UpdateStatus_transitions_and_persists()
    {
        await using var dbContext = await CreateDbContextAsync();
        var store = new MessageProcessingStore(dbContext);
        var request = new CreateMessageProcessingRequest(
            "wamid.status-1",
            "inbound.whatsapp",
            "payload-hash-3",
            "meta",
            new Dictionary<string, string?> { ["providerMessageId"] = "provider-3" });

        var created = await store.CreateAsync(request);
        var updated = await store.UpdateStatusAsync(
            request.MessageId,
            request.MessageType,
            ProcessingStatus.Failed,
            "provider rejected payload");
        var stored = await store.GetAsync(request.MessageId, request.MessageType);

        Assert.Equal(ProcessingStatus.Received, created.Record.Status);
        Assert.Equal(ProcessingStatus.Failed, updated.Status);
        Assert.Equal("provider rejected payload", updated.FailureReason);
        Assert.NotNull(updated.ProcessedAt);
        Assert.True(updated.UpdatedAt >= created.Record.UpdatedAt);
        Assert.NotNull(stored);
        Assert.Equal(ProcessingStatus.Failed, stored!.Status);
        Assert.Equal(updated.ProcessedAt, stored.ProcessedAt);
        Assert.Equal(updated.FailureReason, stored.FailureReason);
    }

    private async Task<MessageBridgeDbContext> CreateDbContextAsync()
    {
        var baseConnectionString = _sharedConnectionString ?? _container!.GetConnectionString();
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = $"messagebridge_tests_{Guid.NewGuid():N}"
        };

        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionStringBuilder.ConnectionString)
            .Options;

        var dbContext = new MessageBridgeDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return dbContext;
    }
}

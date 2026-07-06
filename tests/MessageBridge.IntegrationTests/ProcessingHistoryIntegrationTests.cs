using FluentAssertions;
using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

public sealed class ProcessingHistoryIntegrationTests : IAsyncLifetime
{
    private PostgresFixture? _postgresFixture;
    private MessageBridgeDbContext? _dbContext;
    private IMessageProcessingStore? _store;

    public async Task InitializeAsync()
    {
        _postgresFixture = new PostgresFixture();
        await _postgresFixture.InitializeAsync();
        _dbContext = await _postgresFixture.CreateDbContextAsync();
        _store = new MessageProcessingStore(_dbContext);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext is not null)
        {
            await _dbContext.DisposeAsync();
        }

        if (_postgresFixture is not null)
        {
            await _postgresFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_FirstCreate_ReturnsCreatedOutcome()
    {
        var req = new CreateMessageProcessingRequest(
            "msg-001",
            "whatsapp.send",
            "hash-001",
            "provider-x",
            new Dictionary<string, string?> { ["ref"] = "123" });

        var result = await _store!.CreateAsync(req);

        result.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        result.Record.MessageId.Should().Be("msg-001");
        result.Record.Status.Should().Be(ProcessingStatus.Received);
    }

    [Fact]
    public async Task CreateAsync_DuplicateMessageId_ReturnsDuplicateOutcome()
    {
        var req = new CreateMessageProcessingRequest(
            "msg-dup-001",
            "email.confirm",
            "hash-x",
            "provider-y",
            new Dictionary<string, string?> { ["ref"] = "456" });

        var first = await _store!.CreateAsync(req);
        var second = await _store.CreateAsync(req);

        first.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        second.Outcome.Should().Be(CreateMessageProcessingOutcome.Duplicate);
        first.Record.Id.Should().Be(second.Record.Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_TransitionsFromReceivedToCompleted()
    {
        var msgId = "msg-status-001";
        var msgType = "whatsapp.send";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-y",
            "provider-z",
            new Dictionary<string, string?> { ["ref"] = "789" });

        var created = await _store!.CreateAsync(req);
        created.Record.Status.Should().Be(ProcessingStatus.Received);

        var updated = await _store.UpdateStatusAsync(msgId, msgType, ProcessingStatus.Completed);

        updated.Status.Should().Be(ProcessingStatus.Completed);
        updated.ProcessedAt.Should().NotBeNull();
        updated.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_TransitionsToFailedWithReason()
    {
        var msgId = "msg-fail-001";
        var msgType = "email.confirm";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-z",
            "provider-a",
            new Dictionary<string, string?> { ["ref"] = "abc" });

        var created = await _store!.CreateAsync(req);

        var updated = await _store.UpdateStatusAsync(
            msgId,
            msgType,
            ProcessingStatus.Failed,
            "Provider rate limit exceeded");

        updated.Status.Should().Be(ProcessingStatus.Failed);
        updated.FailureReason.Should().Be("Provider rate limit exceeded");
        updated.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsStoredRecord()
    {
        var msgId = "msg-get-001";
        var msgType = "whatsapp.send";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-a",
            "provider-b",
            new Dictionary<string, string?> { ["ref"] = "def", ["extra"] = "data" });

        await _store!.CreateAsync(req);

        var retrieved = await _store.GetAsync(msgId, msgType);

        retrieved.Should().NotBeNull();
        retrieved!.MessageId.Should().Be(msgId);
        retrieved.MessageType.Should().Be(msgType);
        retrieved.PayloadHash.Should().Be("hash-a");
        retrieved.Provider.Should().Be("provider-b");
        retrieved.ProviderMetadata["ref"].Should().Be("def");
        retrieved.ProviderMetadata["extra"].Should().Be("data");
    }

    [Fact]
    public async Task GetAsync_NonExistentMessage_ReturnsNull()
    {
        var retrieved = await _store!.GetAsync("nonexistent", "fake.type");
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task ProcessingRecords_PersistAttemptCount()
    {
        var msgId = "msg-attempt-001";
        var msgType = "whatsapp.send";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-attempt",
            "provider",
            new Dictionary<string, string?> { ["ref"] = "ghi" });

        var created = await _store!.CreateAsync(req);
        created.Record.AttemptCount.Should().Be(1);

        var retrieved = await _store.GetAsync(msgId, msgType);
        retrieved!.AttemptCount.Should().Be(1);
    }
}

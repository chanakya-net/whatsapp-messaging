using FluentAssertions;
using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.IntegrationTests.Persistence;
using Xunit;

namespace MessageBridge.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ProcessingHistoryIntegrationTests(IntegrationEnvironmentFixture fixture)
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;

    [Fact]
    public async Task CreateAsync_FirstCreate_ReturnsCreatedOutcome()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var result = await CreateStore(scenario).CreateAsync(CreateRequest("msg-001", "whatsapp.send"));

        result.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        result.Record.MessageId.Should().Be("msg-001");
        result.Record.Status.Should().Be(ProcessingStatus.Received);
    }

    [Fact]
    public async Task CreateAsync_DuplicateMessageAndType_ReturnsDuplicateOutcome()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = CreateStore(scenario);
        var request = CreateRequest("msg-dup-001", "email.confirm");

        var first = await store.CreateAsync(request);
        var second = await store.CreateAsync(request);

        first.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        second.Outcome.Should().Be(CreateMessageProcessingOutcome.Duplicate);
        first.Record.Id.Should().Be(second.Record.Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_TransitionsFromReceivedToCompleted()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = CreateStore(scenario);
        var request = CreateRequest("msg-status-001", "whatsapp.send");

        var created = await store.CreateAsync(request);
        var updated = await store.UpdateStatusAsync(
            request.MessageId,
            request.MessageType,
            ProcessingStatus.Completed);

        created.Record.Status.Should().Be(ProcessingStatus.Received);
        updated.Status.Should().Be(ProcessingStatus.Completed);
        updated.ProcessedAt.Should().NotBeNull();
        updated.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_TransitionsToFailedWithReason()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = CreateStore(scenario);
        var request = CreateRequest("msg-fail-001", "email.confirm");

        await store.CreateAsync(request);
        var updated = await store.UpdateStatusAsync(
            request.MessageId,
            request.MessageType,
            ProcessingStatus.Failed,
            "Provider rate limit exceeded");

        updated.Status.Should().Be(ProcessingStatus.Failed);
        updated.FailureReason.Should().Be("Provider rate limit exceeded");
        updated.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsStoredRecordWithMetadata()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = CreateStore(scenario);
        var request = new CreateMessageProcessingRequest(
            "msg-get-001",
            "whatsapp.send",
            "hash-a",
            "provider-b",
            new Dictionary<string, string?> { ["ref"] = "def", ["extra"] = "data" });

        await store.CreateAsync(request);
        var retrieved = await store.GetAsync(request.MessageId, request.MessageType);

        retrieved.Should().NotBeNull();
        retrieved!.MessageId.Should().Be(request.MessageId);
        retrieved.MessageType.Should().Be(request.MessageType);
        retrieved.PayloadHash.Should().Be(request.PayloadHash);
        retrieved.Provider.Should().Be(request.Provider);
        retrieved.ProviderMetadata["ref"].Should().Be("def");
        retrieved.ProviderMetadata["extra"].Should().Be("data");
    }

    [Fact]
    public async Task GetAsync_NonExistentMessage_ReturnsNull()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);

        var retrieved = await CreateStore(scenario).GetAsync("nonexistent", "fake.type");

        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task ProcessingRecords_PersistAttemptCount()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = CreateStore(scenario);
        var request = CreateRequest("msg-attempt-001", "whatsapp.send");

        var created = await store.CreateAsync(request);
        var retrieved = await store.GetAsync(request.MessageId, request.MessageType);

        created.Record.AttemptCount.Should().Be(1);
        retrieved!.AttemptCount.Should().Be(1);
    }

    private static MessageProcessingStore CreateStore(MigratedDatabaseScenario scenario) =>
        new(scenario.DbContext);

    private static CreateMessageProcessingRequest CreateRequest(string messageId, string messageType) =>
        new(
            messageId,
            messageType,
            $"hash-{messageId}",
            "provider",
            new Dictionary<string, string?> { ["ref"] = "123" });
}

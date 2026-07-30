using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageBridge.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class MessageProcessingStoreTests(IntegrationEnvironmentFixture fixture)
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;

    [Fact]
    public async Task CreateAsync_persists_and_is_retrievable()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = new MessageProcessingStore(scenario.DbContext);
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
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(stored.Id, result.Record.Id);
    }

    [Fact]
    public async Task CreateAsync_duplicate_message_and_type_returns_existing()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = new MessageProcessingStore(scenario.DbContext);
        var request = new CreateMessageProcessingRequest(
            "wamid.duplicate-1",
            "inbound.whatsapp",
            "payload-hash-2",
            "meta",
            new Dictionary<string, string?> { ["providerMessageId"] = "provider-2" });

        var first = await store.CreateAsync(request);
        var second = await store.CreateAsync(request);
        var storedCount = await scenario.DbContext.MessageProcessingRecords.CountAsync();

        Assert.Equal(CreateMessageProcessingOutcome.Created, first.Outcome);
        Assert.Equal(CreateMessageProcessingOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, storedCount);
    }

    [Fact]
    public async Task CreateAsync_same_message_with_different_type_creates_distinct_records()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = new MessageProcessingStore(scenario.DbContext);

        var first = await store.CreateAsync(CreateRequest("wamid.pair-1", "inbound.whatsapp"));
        var second = await store.CreateAsync(CreateRequest("wamid.pair-1", "email.confirm"));

        Assert.Equal(CreateMessageProcessingOutcome.Created, first.Outcome);
        Assert.Equal(CreateMessageProcessingOutcome.Created, second.Outcome);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
        Assert.Equal(2, await scenario.DbContext.MessageProcessingRecords.CountAsync());
    }

    [Fact]
    public async Task UpdateStatus_transitions_and_persists_failure_reason_and_timestamp()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var store = new MessageProcessingStore(scenario.DbContext);
        var request = CreateRequest("wamid.status-1", "inbound.whatsapp");

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
        Assert.Equal(updated.FailureReason, stored.FailureReason);
        Assert.Equal(updated.ProcessedAt!.Value, stored.ProcessedAt!.Value, TimeSpan.FromMicroseconds(1));
    }

    private static CreateMessageProcessingRequest CreateRequest(string messageId, string messageType) =>
        new(
            messageId,
            messageType,
            $"hash-{messageId}",
            "provider",
            new Dictionary<string, string?> { ["providerMessageId"] = $"provider-{messageId}" });
}

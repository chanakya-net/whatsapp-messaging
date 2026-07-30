using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit.Sdk;

namespace MessageBridge.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class StaleProcessingRecoveryTests(IntegrationEnvironmentFixture fixture)
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;

    [Fact]
    public async Task StartupRecovery_AbandonsStaleRecords_only()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.AddRange(
            BuildMessage("wamid.stale-1", ProcessingStatus.Processing, now.AddMinutes(-31), now.AddMinutes(-31)),
            BuildMessage("wamid.stale-2", ProcessingStatus.Received, now.AddMinutes(-31), now.AddMinutes(-31)),
            BuildMessage("wamid.recent", ProcessingStatus.Processing, now.AddMinutes(-2), now.AddMinutes(-2)),
            BuildMessage("wamid.done", ProcessingStatus.Completed, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)));
        await scenario.DbContext.SaveChangesAsync();

        var recovery = new StaleProcessingRecoveryService(
            new TestStaleProcessingDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions
            {
                RecoveryEnabled = true,
                StaleThresholdMinutes = 30
            }));
        try
        {
            await recovery.StartAsync(default);
            await WaitUntilStatusAsync(options, "wamid.stale-1", ProcessingStatus.Abandoned);
        }
        finally
        {
            await recovery.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        var staleProcessing = await GetRecordAsync(verifyContext, "wamid.stale-1");
        var staleReceived = await GetRecordAsync(verifyContext, "wamid.stale-2");
        var recent = await GetRecordAsync(verifyContext, "wamid.recent");
        var completed = await GetRecordAsync(verifyContext, "wamid.done");

        Assert.Equal(ProcessingStatus.Abandoned, staleProcessing.Status);
        Assert.Equal(ProcessingStatus.Abandoned, staleReceived.Status);
        Assert.NotNull(staleProcessing.ProcessedAt);
        Assert.NotNull(staleReceived.ProcessedAt);
        Assert.Equal(ProcessingStatus.Processing, recent.Status);
        Assert.Equal(ProcessingStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task StartupRecovery_DoesNotRunWhenDisabled()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.Add(
            BuildMessage("wamid.disabled", ProcessingStatus.Processing, now.AddHours(-1), now.AddHours(-1)));
        await scenario.DbContext.SaveChangesAsync();

        var recovery = new StaleProcessingRecoveryService(
            new TestStaleProcessingDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions { RecoveryEnabled = false }));
        try
        {
            await recovery.StartAsync(default);
        }
        finally
        {
            await recovery.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        var record = await GetRecordAsync(verifyContext, "wamid.disabled");
        Assert.Equal(ProcessingStatus.Processing, record.Status);
    }

    private static async Task<MessageProcessingRecord> GetRecordAsync(
        MessageBridgeDbContext context,
        string messageId)
        => await context.MessageProcessingRecords
            .AsNoTracking()
            .SingleAsync(item => item.MessageId == messageId);

    private static MessageProcessingRecord BuildMessage(
        string messageId,
        ProcessingStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? processedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            MessageType = "inbound.whatsapp",
            Status = status,
            PayloadHash = "payload-hash",
            Provider = "provider",
            ProviderMetadata = JsonDocument.Parse("{}"),
            AttemptCount = 1,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ProcessedAt = processedAt
        };

    private static DbContextOptions<MessageBridgeDbContext> BuildOptions(MessageBridgeDbContext context) =>
        new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(context.Database.GetConnectionString()!)
            .Options;

    private static async Task WaitUntilStatusAsync(
        DbContextOptions<MessageBridgeDbContext> options,
        string messageId,
        ProcessingStatus expectedStatus)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var context = new MessageBridgeDbContext(options);
            var status = await context.MessageProcessingRecords
                .AsNoTracking()
                .Where(item => item.MessageId == messageId)
                .Select(item => item.Status)
                .SingleAsync();
            if (status == expectedStatus)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new XunitException($"Message '{messageId}' did not reach status '{expectedStatus}'.");
    }
}

internal sealed class TestStaleProcessingDbContextFactory(
    DbContextOptions<MessageBridgeDbContext> options)
    : IDbContextFactory<MessageBridgeDbContext>
{
    public MessageBridgeDbContext CreateDbContext() => new(options);

    public ValueTask<MessageBridgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        new(CreateDbContext());
}

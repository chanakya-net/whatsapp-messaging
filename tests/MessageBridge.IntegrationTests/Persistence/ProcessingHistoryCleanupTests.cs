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
public sealed class ProcessingHistoryCleanupTests(IntegrationEnvironmentFixture fixture)
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;

    [Fact]
    public async Task Cleanup_ServiceDeletesTerminalNonFailedRecords()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.AddRange(
            BuildRecord("wamid.completed-old", ProcessingStatus.Completed, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)),
            BuildRecord("wamid.failed-old", ProcessingStatus.Failed, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)),
            BuildRecord("wamid.abandoned-old", ProcessingStatus.Abandoned, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)),
            BuildRecord("wamid.processing-old", ProcessingStatus.Processing, now.AddHours(-2), now.AddHours(-2)),
            BuildRecord("wamid.completed-recent", ProcessingStatus.Completed, now, now, now));
        await scenario.DbContext.SaveChangesAsync();

        var factory = new TestHistoryCleanupDbContextFactory(options);
        var cleanup = CreateCleanup(factory, enabled: true);
        try
        {
            await cleanup.StartAsync(default);
            await WaitUntilRecordRemovedAsync(factory, "wamid.completed-old");
        }
        finally
        {
            await cleanup.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        Assert.False(await ExistsAsync(verifyContext, "wamid.completed-old"));
        Assert.False(await ExistsAsync(verifyContext, "wamid.abandoned-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.failed-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.processing-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.completed-recent"));
    }

    [Fact]
    public async Task Cleanup_ServiceDoesNotDeleteWhenDisabled()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.Add(
            BuildRecord("wamid.should-keep", ProcessingStatus.Completed, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)));
        await scenario.DbContext.SaveChangesAsync();

        var cleanup = CreateCleanup(new TestHistoryCleanupDbContextFactory(options), enabled: false);
        try
        {
            await cleanup.StartAsync(default);
            await Task.Delay(20);
        }
        finally
        {
            await cleanup.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        Assert.True(await ExistsAsync(verifyContext, "wamid.should-keep"));
    }

    private static ProcessingHistoryCleanupService CreateCleanup(
        IDbContextFactory<MessageBridgeDbContext> factory,
        bool enabled) =>
        new(
            factory,
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = enabled,
                CleanupIntervalMilliseconds = 10,
                CleanupRetentionHours = 1,
                CleanupBatchSize = 10
            }));

    private static async Task<bool> ExistsAsync(MessageBridgeDbContext context, string messageId) =>
        await context.MessageProcessingRecords.AsNoTracking().AnyAsync(item => item.MessageId == messageId);

    private static MessageProcessingRecord BuildRecord(
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

    private static async Task WaitUntilRecordRemovedAsync(
        IDbContextFactory<MessageBridgeDbContext> factory,
        string messageId)
    {
        await WaitUntilAsync(
            async () =>
            {
                await using var context = await factory.CreateDbContextAsync();
                return !await ExistsAsync(context, messageId);
            },
            TimeSpan.FromSeconds(1),
            $"Record '{messageId}' was not removed in time.");
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

internal sealed class TestHistoryCleanupDbContextFactory(
    DbContextOptions<MessageBridgeDbContext> options)
    : IDbContextFactory<MessageBridgeDbContext>
{
    public MessageBridgeDbContext CreateDbContext() => new(options);

    public ValueTask<MessageBridgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        new(CreateDbContext());
}

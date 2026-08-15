using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

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

        var factory = new TestHistoryCleanupDbContextFactory(options);
        var cleanup = CreateCleanup(factory, enabled: false);
        try
        {
            await cleanup.StartAsync(default);
            await IntegrationEnvironmentFixture.AssertRemainsAsync(
                async () =>
                {
                    await using var context = await factory.CreateDbContextAsync();
                    return await ExistsAsync(context, "wamid.should-keep");
                },
                TimeSpan.FromMilliseconds(250),
                "Disabled cleanup removed processing history.");
        }
        finally
        {
            await cleanup.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        Assert.True(await ExistsAsync(verifyContext, "wamid.should-keep"));
    }

    [Fact]
    public async Task Cleanup_UsesDevRetentionHours_InDevelopmentEnvironment()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.AddRange(
            BuildRecord("wamid.dev-25hr-old", ProcessingStatus.Completed, now.AddHours(-25), now.AddHours(-25), now.AddHours(-25)),
            BuildRecord("wamid.dev-23hr-old", ProcessingStatus.Completed, now.AddHours(-23), now.AddHours(-23), now.AddHours(-23)),
            BuildRecord("wamid.dev-23hr-abandoned", ProcessingStatus.Abandoned, now.AddHours(-23), now.AddHours(-23), now.AddHours(-23)));
        await scenario.DbContext.SaveChangesAsync();

        var factory = new TestHistoryCleanupDbContextFactory(options);
        var cleanup = new ProcessingHistoryCleanupService(
            factory,
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = true,
                CleanupIntervalMilliseconds = 10,
                DevelopmentRetentionHours = 24,
                ProductionRetentionHours = 168,
                CleanupBatchSize = 10
            }),
            BuildEnvironment("Development"));

        try
        {
            await cleanup.StartAsync(default);
            await WaitUntilRecordRemovedAsync(factory, "wamid.dev-25hr-old");
        }
        finally
        {
            await cleanup.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        Assert.False(await ExistsAsync(verifyContext, "wamid.dev-25hr-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.dev-23hr-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.dev-23hr-abandoned"));
    }

    [Fact]
    public async Task Cleanup_UsesProdRetentionHours_InProductionEnvironment()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.AddRange(
            BuildRecord("wamid.prod-169hr-old", ProcessingStatus.Completed, now.AddHours(-169), now.AddHours(-169), now.AddHours(-169)),
            BuildRecord("wamid.prod-167hr-old", ProcessingStatus.Completed, now.AddHours(-167), now.AddHours(-167), now.AddHours(-167)),
            BuildRecord("wamid.prod-167hr-abandoned", ProcessingStatus.Abandoned, now.AddHours(-167), now.AddHours(-167), now.AddHours(-167)));
        await scenario.DbContext.SaveChangesAsync();

        var factory = new TestHistoryCleanupDbContextFactory(options);
        var cleanup = new ProcessingHistoryCleanupService(
            factory,
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = true,
                CleanupIntervalMilliseconds = 10,
                DevelopmentRetentionHours = 24,
                ProductionRetentionHours = 168,
                CleanupBatchSize = 10
            }),
            BuildEnvironment("Production"));

        try
        {
            await cleanup.StartAsync(default);
            await WaitUntilRecordRemovedAsync(factory, "wamid.prod-169hr-old");
        }
        finally
        {
            await cleanup.StopAsync(default);
        }

        await using var verifyContext = new MessageBridgeDbContext(options);
        Assert.False(await ExistsAsync(verifyContext, "wamid.prod-169hr-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.prod-167hr-old"));
        Assert.True(await ExistsAsync(verifyContext, "wamid.prod-167hr-abandoned"));
    }

    [Fact]
    public async Task Cleanup_PreservesRejectedRecords_WithEligibleStatusFilter()
    {
        await using var scenario = await MigratedDatabaseScenario.CreateAsync(_fixture);
        var options = BuildOptions(scenario.DbContext);
        var now = DateTimeOffset.UtcNow;
        scenario.DbContext.MessageProcessingRecords.AddRange(
            BuildRecord("wamid.completed-old", ProcessingStatus.Completed, now.AddHours(-25), now.AddHours(-25), now.AddHours(-25)),
            BuildRecord("wamid.rejected-old", ProcessingStatus.Rejected, now.AddHours(-25), now.AddHours(-25), now.AddHours(-25)));
        await scenario.DbContext.SaveChangesAsync();

        var factory = new TestHistoryCleanupDbContextFactory(options);
        var cleanup = new ProcessingHistoryCleanupService(
            factory,
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = true,
                CleanupIntervalMilliseconds = 10,
                DevelopmentRetentionHours = 24,
                ProductionRetentionHours = 168,
                EligibleStatusesForCleanup = [ProcessingStatus.Completed, ProcessingStatus.Abandoned],
                CleanupBatchSize = 10
            }),
            BuildEnvironment("Development"));

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
        Assert.True(await ExistsAsync(verifyContext, "wamid.rejected-old"));
    }

    private static ProcessingHistoryCleanupService CreateCleanup(
        IDbContextFactory<MessageBridgeDbContext> factory,
        bool enabled,
        string environment = "Development") =>
        new(
            factory,
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = enabled,
                CleanupIntervalMilliseconds = 10,
                DevelopmentRetentionHours = 1,
                ProductionRetentionHours = 1,
                CleanupBatchSize = 10
            }),
            BuildEnvironment(environment));

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
        await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () =>
            {
                await using var context = await factory.CreateDbContextAsync();
                return !await ExistsAsync(context, messageId);
            },
            $"Record '{messageId}' was not removed in time.");
    }

    private static IHostEnvironment BuildEnvironment(string environmentName) =>
        new TestHostEnvironment(environmentName);
}

internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "test";
    public string ContentRootPath { get; set; } = "/test";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class NullFileProvider : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

internal sealed class NotFoundFileInfo(string name) : IFileInfo
{
    public bool Exists => false;
    public long Length => -1;
    public string PhysicalPath => null!;
    public string Name => name;
    public DateTimeOffset LastModified => DateTimeOffset.MinValue;
    public bool IsDirectory => false;
    public Stream CreateReadStream() => Stream.Null;
}

internal sealed class TestHistoryCleanupDbContextFactory(
    DbContextOptions<MessageBridgeDbContext> options)
    : IDbContextFactory<MessageBridgeDbContext>
{
    public MessageBridgeDbContext CreateDbContext() => new(options);

    public ValueTask<MessageBridgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        new(CreateDbContext());
}

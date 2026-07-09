using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace MessageBridge.Infrastructure.Tests.Persistence;

public sealed class ProcessingHistoryCleanupTests : IAsyncLifetime
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
    public async Task Cleanup_ServiceDeletesTerminalNonFailedRecords()
    {
        var options = BuildOptions(nameof(Cleanup_ServiceDeletesTerminalNonFailedRecords) + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await using (var seedContext = await CreateDbContextAsync(options))
        {
            seedContext.MessageProcessingRecords.AddRange(
                BuildRecord(
                    "wamid.completed-old",
                    ProcessingStatus.Completed,
                    now.AddHours(-2),
                    now.AddHours(-2),
                    now.AddHours(-2)),
                BuildRecord(
                    "wamid.failed-old",
                    ProcessingStatus.Failed,
                    now.AddHours(-2),
                    now.AddHours(-2),
                    now.AddHours(-2)),
                BuildRecord(
                    "wamid.abandoned-old",
                    ProcessingStatus.Abandoned,
                    now.AddHours(-2),
                    now.AddHours(-2),
                    now.AddHours(-2)),
                BuildRecord(
                    "wamid.processing-old",
                    ProcessingStatus.Processing,
                    now.AddHours(-2),
                    now.AddHours(-2),
                    null),
                BuildRecord(
                    "wamid.completed-recent",
                    ProcessingStatus.Completed,
                    now,
                    now,
                    now));
            await seedContext.SaveChangesAsync();
        }

        var cleanup = new ProcessingHistoryCleanupService(
            new TestHistoryCleanupDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = true,
                CleanupIntervalMilliseconds = 10,
                CleanupRetentionHours = 1,
                CleanupBatchSize = 10
            }));

        await cleanup.StartAsync(default);
        await WaitUntilRecordRemovedAsync(new TestHistoryCleanupDbContextFactory(options), "wamid.completed-old");
        await cleanup.StopAsync(default);

        await using var verifyContext = await CreateDbContextAsync(options);
        var completedOld = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.completed-old");
        var abandonedOld = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.abandoned-old");
        var failedOld = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.failed-old");
        var processingOld = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.processing-old");
        var completedRecent = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.completed-recent");

        Assert.False(completedOld);
        Assert.False(abandonedOld);
        Assert.True(failedOld);
        Assert.True(processingOld);
        Assert.True(completedRecent);
    }

    [Fact]
    public async Task Cleanup_ServiceDoesNotDeleteWhenDisabled()
    {
        var options = BuildOptions(nameof(Cleanup_ServiceDoesNotDeleteWhenDisabled) + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await using (var seedContext = await CreateDbContextAsync(options))
        {
            seedContext.MessageProcessingRecords.Add(
                BuildRecord(
                    "wamid.should-keep",
                    ProcessingStatus.Completed,
                    now.AddHours(-2),
                    now.AddHours(-2),
                    now.AddHours(-2)));
            await seedContext.SaveChangesAsync();
        }

        var cleanup = new ProcessingHistoryCleanupService(
            new TestHistoryCleanupDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions
            {
                CleanupEnabled = false,
                CleanupIntervalMilliseconds = 5,
                CleanupRetentionHours = 1,
            }));
        await cleanup.StartAsync(default);
        await Task.Delay(20);
        await cleanup.StopAsync(default);

        await using var verifyContext = await CreateDbContextAsync(options);
        var kept = await verifyContext.MessageProcessingRecords
            .AsNoTracking()
            .AnyAsync(item => item.MessageId == "wamid.should-keep");
        Assert.True(kept);
    }

    private static MessageProcessingRecord BuildRecord(
        string messageId,
        ProcessingStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? processedAt = null)
    {
        return new MessageProcessingRecord
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
    }

    private async Task<MessageBridgeDbContext> CreateDbContextAsync(DbContextOptions<MessageBridgeDbContext> options)
    {
        var context = new MessageBridgeDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private DbContextOptions<MessageBridgeDbContext> BuildOptions(string databaseName)
    {
        var baseConnectionString = _sharedConnectionString ?? _container!.GetConnectionString();
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName
        };

        return new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionStringBuilder.ConnectionString)
            .Options;
    }

    private static async Task WaitUntilRecordRemovedAsync(
        TestHistoryCleanupDbContextFactory factory,
        string messageId)
    {
        await WaitUntilAsync(
            async () =>
            {
                await using var context = await factory.CreateDbContextAsync();
                return !await context.MessageProcessingRecords.AnyAsync(item => item.MessageId == messageId);
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

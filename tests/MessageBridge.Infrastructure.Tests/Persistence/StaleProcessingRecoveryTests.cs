using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace MessageBridge.Infrastructure.Tests.Persistence;

public sealed class StaleProcessingRecoveryTests : IAsyncLifetime
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
    public async Task StartupRecovery_AbandonsStaleProcessingRecords()
    {
        var options = BuildOptions(nameof(StartupRecovery_AbandonsStaleProcessingRecords) + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await using (var seedContext = await CreateDbContextAsync(options))
        {
            seedContext.MessageProcessingRecords.AddRange(
                BuildMessage("wamid.stale-1", ProcessingStatus.Processing, now.AddMinutes(-31), now.AddMinutes(-31)),
                BuildMessage("wamid.stale-2", ProcessingStatus.Received, now.AddMinutes(-31), now.AddMinutes(-31)),
                BuildMessage("wamid.recent", ProcessingStatus.Processing, now.AddMinutes(-2), now.AddMinutes(-2)),
                BuildMessage("wamid.done", ProcessingStatus.Completed, now.AddHours(-2), now.AddHours(-2), now.AddHours(-2)));
            await seedContext.SaveChangesAsync();
        }

        var recovery = new StaleProcessingRecoveryService(
            new TestStaleProcessingDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions { RecoveryEnabled = true, StaleThresholdMinutes = 30 }));
        await recovery.StartAsync(default);
        await WaitUntilRecordStatusAsync(
            options,
            "wamid.stale-1",
            ProcessingStatus.Abandoned,
            TimeSpan.FromSeconds(1));
        await recovery.StopAsync(default);

        await using var verifyContext = await CreateDbContextAsync(options);
        var staleProcessing = await verifyContext.MessageProcessingRecords.SingleAsync(item => item.MessageId == "wamid.stale-1");
        var staleReceived = await verifyContext.MessageProcessingRecords.SingleAsync(item => item.MessageId == "wamid.stale-2");
        var recent = await verifyContext.MessageProcessingRecords.SingleAsync(item => item.MessageId == "wamid.recent");
        var completed = await verifyContext.MessageProcessingRecords.SingleAsync(item => item.MessageId == "wamid.done");

        Assert.Equal(ProcessingStatus.Abandoned, staleProcessing.Status);
        Assert.Equal(ProcessingStatus.Abandoned, staleReceived.Status);
        Assert.Equal(ProcessingStatus.Processing, recent.Status);
        Assert.Equal(ProcessingStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task StartupRecovery_DoesNotRunWhenDisabled()
    {
        var options = BuildOptions(nameof(StartupRecovery_DoesNotRunWhenDisabled) + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await using (var seedContext = await CreateDbContextAsync(options))
        {
            seedContext.MessageProcessingRecords.Add(
                BuildMessage("wamid.disabled", ProcessingStatus.Processing, now.AddHours(-1), now.AddHours(-1)));
            await seedContext.SaveChangesAsync();
        }

        var recovery = new StaleProcessingRecoveryService(
            new TestStaleProcessingDbContextFactory(options),
            Options.Create(new MessageProcessingHistoryOptions { RecoveryEnabled = false }));
        await recovery.StartAsync(default);
        await recovery.StopAsync(default);

        await using var verifyContext = await CreateDbContextAsync(options);
        var record = await verifyContext.MessageProcessingRecords.SingleAsync(item => item.MessageId == "wamid.disabled");

        Assert.Equal(ProcessingStatus.Processing, record.Status);
    }

    private static MessageProcessingRecord BuildMessage(
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

    private static async Task WaitUntilRecordStatusAsync(
        DbContextOptions<MessageBridgeDbContext> options,
        string messageId,
        ProcessingStatus expectedStatus,
        TimeSpan timeout)
    {
        await WaitUntilAsync(
            async () =>
            {
                await using var context = new MessageBridgeDbContext(options);
                var status = await context.MessageProcessingRecords
                    .AsNoTracking()
                    .Where(item => item.MessageId == messageId)
                    .Select(item => item.Status)
                    .SingleAsync();
                return status == expectedStatus;
            },
            timeout,
            $"Message '{messageId}' did not reach status '{expectedStatus}'.");
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

internal sealed class TestStaleProcessingDbContextFactory(
    DbContextOptions<MessageBridgeDbContext> options)
    : IDbContextFactory<MessageBridgeDbContext>
{
    public MessageBridgeDbContext CreateDbContext() => new(options);

    public ValueTask<MessageBridgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        new(CreateDbContext());
}

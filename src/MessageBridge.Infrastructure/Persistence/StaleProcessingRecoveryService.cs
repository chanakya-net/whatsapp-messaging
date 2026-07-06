using MessageBridge.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class StaleProcessingRecoveryService(
    IDbContextFactory<MessageBridgeDbContext> contextFactory,
    IOptions<MessageProcessingHistoryOptions> options)
    : BackgroundService
{
    private static readonly ProcessingStatus[] StaleStatuses =
        [ProcessingStatus.Received, ProcessingStatus.Processing];

    private readonly MessageProcessingHistoryOptions _options = options.Value;
    private readonly IDbContextFactory<MessageBridgeDbContext> _contextFactory = contextFactory;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!_options.RecoveryEnabled)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var threshold = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(_options.StaleThresholdMinutes);
        var stale = await context.Set<MessageProcessingRecord>()
            .Where(record => StaleStatuses.Contains(record.Status) && record.UpdatedAt < threshold)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var record in stale)
        {
            record.Status = ProcessingStatus.Abandoned;
            record.UpdatedAt = now;
            record.ProcessedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

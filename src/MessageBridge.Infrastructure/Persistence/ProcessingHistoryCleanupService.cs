using MessageBridge.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class ProcessingHistoryCleanupService : BackgroundService
{
    private readonly MessageProcessingHistoryOptions _options;
    private readonly IDbContextFactory<MessageBridgeDbContext> _contextFactory;
    private readonly IHostEnvironment _environment;
    private readonly TimeSpan _interval;

    public ProcessingHistoryCleanupService(
        IDbContextFactory<MessageBridgeDbContext> contextFactory,
        IOptions<MessageProcessingHistoryOptions> options,
        IHostEnvironment environment)
    {
        _contextFactory = contextFactory;
        _options = options.Value;
        _environment = environment;
        _interval = TimeSpan.FromMilliseconds(_options.CleanupIntervalMilliseconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (!_options.CleanupEnabled)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var retentionHours = _environment.IsProduction()
            ? _options.ProductionRetentionHours
            : _options.DevelopmentRetentionHours;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(retentionHours);
        var stale = await context.Set<MessageProcessingRecord>()
            .Where(record =>
                _options.EligibleStatusesForCleanup.Contains(record.Status) &&
                record.ProcessedAt != null &&
                record.ProcessedAt < cutoff)
            .OrderBy(record => record.ProcessedAt)
            .Take(_options.CleanupBatchSize)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        context.RemoveRange(stale);
        await context.SaveChangesAsync(cancellationToken);
    }
}

using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher.EntityFrameworkCore;

public sealed class MessageBridgeOutboxCleanupHostedService<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;
    private readonly MessageBridgeOutboxOptions _options;
    private readonly TimeSpan _interval;

    public MessageBridgeOutboxCleanupHostedService(
        IDbContextFactory<TContext> contextFactory,
        IOptions<MessageBridgeOutboxOptions> options)
    {
        _contextFactory = contextFactory;
        _options = options.Value;
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
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(_options.CleanupRetentionHours);

        var stale = await context.Set<MessageBridgeOutboxMessage>()
            .Where(message => message.PublishedAtUtc != null && message.PublishedAtUtc < cutoff)
            .OrderBy(message => message.PublishedAtUtc)
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


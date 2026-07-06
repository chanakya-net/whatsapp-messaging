using System.Collections.Concurrent;
using System.Text.Json;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using MessageBridge.Publisher.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher.EntityFrameworkCore;

public sealed class MessageBridgeOutboxDispatcherHostedService<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;
    private readonly IMessageBridgePublisherTransport _transport;
    private readonly MessageBridgeOutboxOptions _options;
    private readonly TimeSpan _pollDelay;
    private readonly TimeSpan _retryDelay;

    public MessageBridgeOutboxDispatcherHostedService(
        IDbContextFactory<TContext> contextFactory,
        IMessageBridgePublisherTransport transport,
        IOptions<MessageBridgeOutboxOptions> options)
    {
        _contextFactory = contextFactory;
        _transport = transport;
        _options = options.Value;
        _pollDelay = TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);
        _retryDelay = TimeSpan.FromMilliseconds(_options.RetryDelayMilliseconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchPendingMessagesAsync(stoppingToken);
            await Task.Delay(_pollDelay, stoppingToken);
        }
    }

    private async Task DispatchPendingMessagesAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await context.Set<MessageBridgeOutboxMessage>()
            .Where(message => message.PublishedAtUtc == null)
            .Take(_options.BatchSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var successfulIds = await PublishPendingAsync(pending, cancellationToken);
        if (successfulIds.Length == 0)
        {
            return;
        }

        await using var updateContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var messageId in successfulIds)
        {
            var message = await updateContext.Set<MessageBridgeOutboxMessage>()
                .FindAsync(new object[] { messageId }, cancellationToken);

            if (message is not null)
            {
                message.PublishedAtUtc = now;
            }
        }

        await updateContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string[]> PublishPendingAsync(
        IReadOnlyCollection<MessageBridgeOutboxMessage> pending,
        CancellationToken cancellationToken)
    {
        var published = new ConcurrentBag<string>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.Concurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(pending, options, async (message, token) =>
        {
            var wasPublished = await PublishWithRetriesAsync(message, token);
            if (wasPublished)
            {
                published.Add(message.Id);
            }
        });

        return [.. published];
    }

    private async Task<bool> PublishWithRetriesAsync(MessageBridgeOutboxMessage message, CancellationToken cancellationToken)
    {
        var retryDelay = _retryDelay;
        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    retryDelay.TotalMilliseconds * _options.RetryBackoffMultiplier,
                    5_000));
            }

            try
            {
                var envelope = new MessageBridgePublisherEnvelope(
                    message.ExchangeName,
                    message.RoutingKey,
                    message.MessageId,
                    message.CorrelationId,
                    ParseHeaders(message.Headers),
                    message.Payload);

                await _transport.PublishAsync(envelope, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                if (attempt >= _options.MaxRetryAttempts)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseHeaders(string headers)
    {
        if (string.IsNullOrWhiteSpace(headers))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}

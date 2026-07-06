using System.ComponentModel.DataAnnotations;

namespace MessageBridge.Publisher.EntityFrameworkCore;

public sealed class MessageBridgeOutboxOptions
{
    [Range(1, 5000)]
    public int BatchSize { get; set; } = 100;

    [Range(1, 128)]
    public int Concurrency { get; set; } = 4;

    [Range(1, 10_000)]
    public int PollIntervalMilliseconds { get; set; } = 500;

    [Range(0, 20)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(1, 10_000)]
    public int RetryDelayMilliseconds { get; set; } = 50;

    [Range(1.0, 20.0)]
    public double RetryBackoffMultiplier { get; set; } = 2.0;

    public bool CleanupEnabled { get; set; }

    [Range(1, 3650)]
    public int CleanupRetentionHours { get; set; } = 24;

    [Range(1, 10_000)]
    public int CleanupBatchSize { get; set; } = 500;

    [Range(1, 3_600_000)]
    public int CleanupIntervalMilliseconds { get; set; } = 1_000;
}

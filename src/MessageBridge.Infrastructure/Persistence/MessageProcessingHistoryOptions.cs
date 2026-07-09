using System.ComponentModel.DataAnnotations;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageProcessingHistoryOptions
{
    public const string SectionName = "MessageBridge:ProcessingHistory";

    public bool RecoveryEnabled { get; set; } = true;

    [Range(1, 5_000)]
    public int StaleThresholdMinutes { get; set; } = 30;

    public bool CleanupEnabled { get; set; }

    [Range(1, 3_650)]
    public int CleanupRetentionHours { get; set; } = 24;

    [Range(1, 10_000)]
    public int CleanupBatchSize { get; set; } = 500;

    [Range(1, 3_600_000)]
    public int CleanupIntervalMilliseconds { get; set; } = 1_000;
}

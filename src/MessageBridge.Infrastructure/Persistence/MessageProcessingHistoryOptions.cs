using System.ComponentModel.DataAnnotations;
using MessageBridge.Domain.Processing;

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

    [Range(1, 3_650)]
    public int DevelopmentRetentionHours { get; set; } = 24;

    [Range(1, 3_650)]
    public int ProductionRetentionHours { get; set; } = 168;

    private ProcessingStatus[] _eligibleStatusesForCleanup = [ProcessingStatus.Completed, ProcessingStatus.Abandoned];

    public ProcessingStatus[] EligibleStatusesForCleanup
    {
        get => _eligibleStatusesForCleanup;
        set => _eligibleStatusesForCleanup = value is null
            ? []
            : [..value.Where(s => s is ProcessingStatus.Completed or ProcessingStatus.Abandoned).Distinct()];
    }
}

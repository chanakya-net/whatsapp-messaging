namespace MessageBridge.Infrastructure.Messaging.Options;

public sealed class TransportRetryOptions
{
    public const string SectionName = "MessageBridge:TransportRetry";
    public static readonly TimeSpan[] DefaultDelayedRedeliveryIntervals =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1)
    ];

    public int ImmediateRetryCount { get; set; } = 3;

    public TimeSpan[] DelayedRedeliveryIntervals { get; set; } = [];
}

namespace MessageBridge.Domain.Processing;

public enum ProcessingStatus
{
    Received = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Abandoned = 4
}

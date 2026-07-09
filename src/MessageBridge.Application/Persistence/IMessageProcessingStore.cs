using MessageBridge.Domain.Processing;

namespace MessageBridge.Application.Persistence;

public interface IMessageProcessingStore
{
    Task<CreateMessageProcessingResult> CreateAsync(
        CreateMessageProcessingRequest request,
        CancellationToken cancellationToken = default);

    Task<MessageProcessingSnapshot?> GetAsync(
        string messageId,
        string messageType,
        CancellationToken cancellationToken = default);

    Task<MessageProcessingSnapshot> UpdateStatusAsync(
        string messageId,
        string messageType,
        ProcessingStatus status,
        string? failureReason = null,
        CancellationToken cancellationToken = default);
}

public sealed record CreateMessageProcessingRequest(
    string MessageId,
    string MessageType,
    string PayloadHash,
    string Provider,
    IReadOnlyDictionary<string, string?> ProviderMetadata);

public sealed record CreateMessageProcessingResult(
    CreateMessageProcessingOutcome Outcome,
    MessageProcessingSnapshot Record);

public enum CreateMessageProcessingOutcome
{
    Created = 0,
    Duplicate = 1
}

public sealed record MessageProcessingSnapshot(
    Guid Id,
    string MessageId,
    string MessageType,
    ProcessingStatus Status,
    string PayloadHash,
    string Provider,
    IReadOnlyDictionary<string, string?> ProviderMetadata,
    string? FailureReason,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ProcessedAt);

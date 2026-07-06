using System.Text.Json;
using MessageBridge.Domain.Processing;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageProcessingRecord
{
    public Guid Id { get; set; }

    public string MessageId { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public ProcessingStatus Status { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public JsonDocument ProviderMetadata { get; set; } = JsonDocument.Parse("{}");

    public string? FailureReason { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

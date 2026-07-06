using System.Text.Json;
using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageProcessingStore(MessageBridgeDbContext dbContext) : IMessageProcessingStore
{
    public async Task<CreateMessageProcessingResult> CreateAsync(
        CreateMessageProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new MessageProcessingRecord
        {
            Id = Guid.NewGuid(),
            MessageId = request.MessageId,
            MessageType = request.MessageType,
            Status = ProcessingStatus.Received,
            PayloadHash = request.PayloadHash,
            Provider = request.Provider,
            ProviderMetadata = CreateMetadataDocument(request.ProviderMetadata),
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.MessageProcessingRecords.Add(record);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new CreateMessageProcessingResult(CreateMessageProcessingOutcome.Created, ToSnapshot(record));
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            DetachEntries(exception);
            var existing = await GetRequiredAsync(request.MessageId, request.MessageType, cancellationToken);
            return new CreateMessageProcessingResult(CreateMessageProcessingOutcome.Duplicate, existing);
        }
    }

    public async Task<MessageProcessingSnapshot?> GetAsync(
        string messageId,
        string messageType,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.MessageProcessingRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.MessageType == messageType,
                cancellationToken);

        return record is null ? null : ToSnapshot(record);
    }

    public async Task<MessageProcessingSnapshot> UpdateStatusAsync(
        string messageId,
        string messageType,
        ProcessingStatus status,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.MessageProcessingRecords.SingleAsync(
            item => item.MessageId == messageId && item.MessageType == messageType,
            cancellationToken);

        record.Status = status;
        record.FailureReason = failureReason;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.ProcessedAt = status is ProcessingStatus.Completed or ProcessingStatus.Failed or ProcessingStatus.Abandoned
            ? record.UpdatedAt
            : null;

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSnapshot(record);
    }

    private static JsonDocument CreateMetadataDocument(IReadOnlyDictionary<string, string?> metadata)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(metadata));
    }

    private static void DetachEntries(DbUpdateException exception)
    {
        foreach (var entry in exception.Entries)
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private static MessageProcessingSnapshot ToSnapshot(MessageProcessingRecord record)
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string?>>(
            record.ProviderMetadata.RootElement.GetRawText()) ?? [];

        return new MessageProcessingSnapshot(
            record.Id,
            record.MessageId,
            record.MessageType,
            record.Status,
            record.PayloadHash,
            record.Provider,
            metadata,
            record.FailureReason,
            record.AttemptCount,
            record.CreatedAt,
            record.UpdatedAt,
            record.ProcessedAt);
    }

    private async Task<MessageProcessingSnapshot> GetRequiredAsync(
        string messageId,
        string messageType,
        CancellationToken cancellationToken)
    {
        return await GetAsync(messageId, messageType, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Expected existing processing record for '{messageId}' and '{messageType}'.");
    }
}

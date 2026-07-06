using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Privacy;
using MessageBridge.Domain.Processing;

namespace MessageBridge.Infrastructure.Messaging.Processing;

public sealed class MessageProcessingCoordinator(IMessageProcessingStore processingStore)
{
    public async Task<bool> ProcessAsync(
        string messageId,
        string messageType,
        string payloadHash,
        string provider,
        IReadOnlyDictionary<string, string?> providerMetadata,
        Func<CancellationToken, Task> dispatchAsync,
        CancellationToken cancellationToken = default)
    {
        var createRequest = new CreateMessageProcessingRequest(
            messageId,
            messageType,
            payloadHash,
            provider,
            providerMetadata);

        var createResult = await processingStore.CreateAsync(createRequest, cancellationToken);
        if (createResult.Outcome == CreateMessageProcessingOutcome.Duplicate
            && createResult.Record.Status == ProcessingStatus.Completed)
        {
            return false;
        }

        await processingStore.UpdateStatusAsync(
            messageId,
            messageType,
            ProcessingStatus.Processing,
            cancellationToken: cancellationToken);

        try
        {
            await dispatchAsync(cancellationToken);
            await processingStore.UpdateStatusAsync(
                messageId,
                messageType,
                ProcessingStatus.Completed,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            await processingStore.UpdateStatusAsync(
                messageId,
                messageType,
                ProcessingStatus.Failed,
                ErrorSanitizer.Sanitize(exception.Message),
                cancellationToken);
            throw;
        }
    }
}

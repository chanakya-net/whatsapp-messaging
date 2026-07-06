using ErrorOr;
using MessageBridge.Application.Abstractions;

namespace MessageBridge.Infrastructure.Persistence;

internal sealed class LegacyMessageProcessingStoreAdapter : IMessageProcessingStore
{
    public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
    {
        return Task.FromResult<ErrorOr<Success>>(new Success());
    }
}

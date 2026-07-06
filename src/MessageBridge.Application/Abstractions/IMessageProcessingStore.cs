using ErrorOr;

namespace MessageBridge.Application.Abstractions;

public interface IMessageProcessingStore
{
    Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId);
}

namespace MessageBridge.Publisher.EntityFrameworkCore.Outbox;

public interface IMessageBridgeOutboxWriter
{
    Task WriteAsync(MessageBridgeOutboxMessage message, CancellationToken cancellationToken = default);
}

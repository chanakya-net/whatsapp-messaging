using System.Collections.Generic;

namespace MessageBridge.Publisher.Internal;

public sealed record MessageBridgePublisherEnvelope(
    string ExchangeName,
    string RoutingKey,
    string MessageId,
    string CorrelationId,
    IDictionary<string, string> Headers,
    byte[] Payload);

public interface IMessageBridgePublisherTransport
{
    Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken);
}

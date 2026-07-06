using System.Collections.Generic;

namespace MessageBridge.Publisher.Internal;

internal sealed record MessageBridgePublisherEnvelope(
    string ExchangeName,
    string RoutingKey,
    string MessageId,
    string CorrelationId,
    IDictionary<string, string> Headers,
    byte[] Payload);

internal interface IMessageBridgePublisherTransport
{
    Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken);
}

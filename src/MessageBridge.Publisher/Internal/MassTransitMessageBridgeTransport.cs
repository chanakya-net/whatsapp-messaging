using MassTransit;

namespace MessageBridge.Publisher.Internal;

internal sealed class MassTransitMessageBridgeTransport : IMessageBridgePublisherTransport
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitMessageBridgeTransport(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken)
    {
        return _publishEndpoint.Publish(new MessageBridgePayloadEnvelope
        {
            ExchangeName = envelope.ExchangeName,
            RoutingKey = envelope.RoutingKey,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            Headers = envelope.Headers,
            Payload = envelope.Payload,
        },
        context =>
        {
            context.Headers.Set("exchange", envelope.ExchangeName);
            context.Headers.Set("routing-key", envelope.RoutingKey);
            context.Headers.Set("message-id", envelope.MessageId);
            context.Headers.Set("correlation-id", envelope.CorrelationId);

            foreach (var header in envelope.Headers)
            {
                context.Headers.Set(header.Key, header.Value);
            }
        },
        cancellationToken);
    }

    private sealed class MessageBridgePayloadEnvelope
    {
        public string ExchangeName { get; set; } = string.Empty;
        public string RoutingKey { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}

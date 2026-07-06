using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageFaultConsumer(
    MessageProcessingCoordinator coordinator,
    ISendEndpointProvider sendEndpointProvider,
    IOptions<MessageBridgeTopologyOptions> topologyOptions) : IConsumer<Fault<SendWhatsAppMessageCommand>>
{
    public Task Consume(ConsumeContext<Fault<SendWhatsAppMessageCommand>> context)
    {
        return ConsumeInternalAsync(context);
    }

    private async Task ConsumeInternalAsync(ConsumeContext<Fault<SendWhatsAppMessageCommand>> context)
    {
        await coordinator.MarkFailedAsync<SendWhatsAppMessageCommand>(
            context.Message,
            context.CancellationToken);

        var endpoint = await sendEndpointProvider.GetSendEndpoint(
            new Uri($"queue:{BuildQueueName(topologyOptions.Value.EnvironmentPrefix, "send-whats-app-message_error")}"));
        await endpoint.Send(context.Message.Message, context.CancellationToken);
    }

    private static string BuildQueueName(string prefix, string queueName)
    {
        return string.IsNullOrWhiteSpace(prefix) ? queueName : $"{prefix}-{queueName}";
    }
}

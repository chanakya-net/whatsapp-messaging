using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationFaultConsumer(
    MessageProcessingCoordinator coordinator,
    ISendEndpointProvider sendEndpointProvider,
    IOptions<MessageBridgeTopologyOptions> topologyOptions) : IConsumer<Fault<SendEmailConfirmationCommand>>
{
    public Task Consume(ConsumeContext<Fault<SendEmailConfirmationCommand>> context)
    {
        return ConsumeInternalAsync(context);
    }

    private async Task ConsumeInternalAsync(ConsumeContext<Fault<SendEmailConfirmationCommand>> context)
    {
        await coordinator.MarkFailedAsync<SendEmailConfirmationCommand>(
            context.Message,
            context.CancellationToken);

        var endpoint = await sendEndpointProvider.GetSendEndpoint(
            new Uri($"queue:{BuildQueueName(topologyOptions.Value.EnvironmentPrefix, "send-email-confirmation_error")}"));
        await endpoint.Send(context.Message.Message, context.CancellationToken);
    }

    private static string BuildQueueName(string prefix, string queueName)
    {
        return string.IsNullOrWhiteSpace(prefix) ? queueName : $"{prefix}-{queueName}";
    }
}

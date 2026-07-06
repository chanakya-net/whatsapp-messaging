using MassTransit;
using MessageBridge.Contracts.V1;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageFaultConsumer(
    MessageProcessingCoordinator coordinator) : IConsumer<Fault<SendWhatsAppMessageCommand>>
{
    public Task Consume(ConsumeContext<Fault<SendWhatsAppMessageCommand>> context)
    {
        return coordinator.MarkFailedAsync<SendWhatsAppMessageCommand>(
            context.Message,
            context.CancellationToken);
    }
}

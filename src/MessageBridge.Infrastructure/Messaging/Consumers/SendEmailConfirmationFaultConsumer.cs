using MassTransit;
using MessageBridge.Contracts.V1;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationFaultConsumer(
    MessageProcessingCoordinator coordinator) : IConsumer<Fault<SendEmailConfirmationCommand>>
{
    public Task Consume(ConsumeContext<Fault<SendEmailConfirmationCommand>> context)
    {
        return coordinator.MarkFailedAsync<SendEmailConfirmationCommand>(
            context.Message,
            context.CancellationToken);
    }
}

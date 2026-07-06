using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationConsumer : IConsumer<SendEmailConfirmationCommand>
{
    private readonly IMessageBus _messageBus;

    public SendEmailConfirmationConsumer(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        await _messageBus.SendAsync(context.Message.ToApplicationCommand());
    }
}

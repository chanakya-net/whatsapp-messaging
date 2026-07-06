using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageConsumer : IConsumer<SendWhatsAppMessageCommand>
{
    private readonly IMessageBus _messageBus;

    public SendWhatsAppMessageConsumer(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        await _messageBus.SendAsync(context.Message.ToApplicationCommand());
    }
}

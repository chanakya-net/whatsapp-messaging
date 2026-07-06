using ErrorOr;
using FluentValidation;
using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using MessageBridge.Application.Messages;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageConsumer : IConsumer<SendWhatsAppMessageCommand>
{
    private readonly MessageProcessingCoordinator _coordinator;
    private readonly IValidator<SendWhatsAppMessage> _validator;

    public SendWhatsAppMessageConsumer(
        MessageProcessingCoordinator coordinator,
        IValidator<SendWhatsAppMessage> validator)
    {
        _coordinator = coordinator;
        _validator = validator;
    }

    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        await _coordinator.ConsumeAsync(
            context,
            context.Message.ToApplicationCommand(),
            _validator,
            context.CancellationToken);
    }
}

using ErrorOr;
using FluentValidation;
using MassTransit;
using MessageBridge.Application.Messages;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationConsumer : IConsumer<SendEmailConfirmationCommand>
{
    private readonly MessageProcessingCoordinator _coordinator;
    private readonly IValidator<SendEmailConfirmation> _validator;

    public SendEmailConfirmationConsumer(
        MessageProcessingCoordinator coordinator,
        IValidator<SendEmailConfirmation> validator)
    {
        _coordinator = coordinator;
        _validator = validator;
    }

    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        await _coordinator.ConsumeAsync(
            context,
            context.Message.ToApplicationCommand(),
            _validator,
            context.CancellationToken);
    }
}

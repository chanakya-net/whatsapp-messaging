using ErrorOr;
using FluentValidation;
using MassTransit;
using MessageBridge.Application.Messages;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationConsumer : IConsumer<SendEmailConfirmationCommand>
{
    private readonly MessageProcessingCoordinator _coordinator;
    private readonly IValidator<SendEmailConfirmation> _validator;
    private readonly ILogger<SendEmailConfirmationConsumer> _logger;

    public SendEmailConfirmationConsumer(
        MessageProcessingCoordinator coordinator,
        IValidator<SendEmailConfirmation> validator,
        ILogger<SendEmailConfirmationConsumer> logger)
    {
        _coordinator = coordinator;
        _validator = validator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        using var _ = _logger.BeginScope(ConsumerLifecycleMetadata.ForEmailConfirmation(context.Message));

        await _coordinator.ConsumeAsync(
            context,
            context.Message.ToApplicationCommand(),
            _validator,
            context.CancellationToken);
    }
}

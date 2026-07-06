using ErrorOr;
using FluentValidation;
using MassTransit;
using MessageBridge.Application.Messages;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageConsumer : IConsumer<SendWhatsAppMessageCommand>
{
    private readonly MessageProcessingCoordinator _coordinator;
    private readonly IValidator<SendWhatsAppMessage> _validator;
    private readonly ILogger<SendWhatsAppMessageConsumer> _logger;

    public SendWhatsAppMessageConsumer(
        MessageProcessingCoordinator coordinator,
        IValidator<SendWhatsAppMessage> validator,
        ILogger<SendWhatsAppMessageConsumer> logger)
    {
        _coordinator = coordinator;
        _validator = validator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        using var _ = _logger.BeginScope(ConsumerLifecycleMetadata.ForWhatsApp(context.Message));

        await _coordinator.ConsumeAsync(
            context,
            context.Message.ToApplicationCommand(),
            _validator,
            context.CancellationToken);
    }
}

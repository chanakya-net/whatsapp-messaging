using ErrorOr;
using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageConsumer : IConsumer<SendWhatsAppMessageCommand>
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<SendWhatsAppMessageConsumer> _logger;

    public SendWhatsAppMessageConsumer(
        IMessageBus messageBus,
        ILogger<SendWhatsAppMessageConsumer> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        using var _ = _logger.BeginScope(ConsumerLifecycleMetadata.ForWhatsApp(context.Message));

        var result = await _messageBus.InvokeAsync<ErrorOr<Success>>(
            context.Message.ToApplicationCommand(),
            context.CancellationToken);

        ConsumerDispatchFailure.ThrowIfError(nameof(SendWhatsAppMessageCommand), result);
    }
}

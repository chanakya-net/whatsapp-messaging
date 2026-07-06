using ErrorOr;
using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationConsumer : IConsumer<SendEmailConfirmationCommand>
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<SendEmailConfirmationConsumer> _logger;

    public SendEmailConfirmationConsumer(
        IMessageBus messageBus,
        ILogger<SendEmailConfirmationConsumer> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        using var _ = _logger.BeginScope(ConsumerLifecycleMetadata.ForEmailConfirmation(context.Message));

        var result = await _messageBus.InvokeAsync<ErrorOr<Success>>(
            context.Message.ToApplicationCommand(),
            context.CancellationToken);

        ConsumerDispatchFailure.ThrowIfError(nameof(SendEmailConfirmationCommand), result);
    }
}

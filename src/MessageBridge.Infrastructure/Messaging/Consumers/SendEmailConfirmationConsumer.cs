using ErrorOr;
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
        var result = await _messageBus.InvokeAsync<ErrorOr<Success>>(
            context.Message.ToApplicationCommand(),
            context.CancellationToken);

        ConsumerDispatchFailure.ThrowIfError(nameof(SendEmailConfirmationCommand), result);
    }
}

using ErrorOr;
using MassTransit;
using Google.Protobuf;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Messaging.Mappers;
using System.Security.Cryptography;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendEmailConfirmationConsumer : IConsumer<SendEmailConfirmationCommand>
{
    private readonly IMessageBus _messageBus;
    private readonly MessageProcessingCoordinator _processingCoordinator;

    public SendEmailConfirmationConsumer(
        IMessageBus messageBus,
        MessageProcessingCoordinator processingCoordinator)
    {
        _messageBus = messageBus;
        _processingCoordinator = processingCoordinator;
    }

    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        var payloadHash = GetPayloadHash(context.Message);
        await _processingCoordinator.ProcessAsync(
            context.Message.MessageId,
            "SendEmailConfirmation",
            payloadHash,
            "MessageBridge.Worker",
            new Dictionary<string, string?>
            {
                ["tenantId"] = context.Message.TenantId
            },
            async cancellationToken =>
            {
                var result = await _messageBus.InvokeAsync<ErrorOr<Success>>(
                    context.Message.ToApplicationCommand(),
                    cancellationToken);

                ConsumerDispatchFailure.ThrowIfError(nameof(SendEmailConfirmationCommand), result);
            },
            context.CancellationToken);
    }

    private static string GetPayloadHash(SendEmailConfirmationCommand message)
    {
        var bytes = message.ToByteArray();
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}

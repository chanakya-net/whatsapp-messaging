using ErrorOr;
using MassTransit;
using Google.Protobuf;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Wolverine;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class SendWhatsAppMessageConsumer : IConsumer<SendWhatsAppMessageCommand>
{
    private readonly IMessageBus _messageBus;
    private readonly MessageProcessingCoordinator _processingCoordinator;
    private readonly ILogger<SendWhatsAppMessageConsumer> _logger;

    public SendWhatsAppMessageConsumer(
        IMessageBus messageBus,
        MessageProcessingCoordinator processingCoordinator,
        ILogger<SendWhatsAppMessageConsumer> logger)
    {
        _messageBus = messageBus;
        _processingCoordinator = processingCoordinator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        using var _ = _logger.BeginScope(ConsumerLifecycleMetadata.ForWhatsApp(context.Message));

        var payloadHash = GetPayloadHash(context.Message);
        await _processingCoordinator.ProcessAsync(
            context.Message.MessageId,
            "SendWhatsAppMessage",
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

                ConsumerDispatchFailure.ThrowIfError(nameof(SendWhatsAppMessageCommand), result);
            },
            context.CancellationToken);
    }

    private static string GetPayloadHash(SendWhatsAppMessageCommand message)
    {
        var bytes = message.ToByteArray();
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}

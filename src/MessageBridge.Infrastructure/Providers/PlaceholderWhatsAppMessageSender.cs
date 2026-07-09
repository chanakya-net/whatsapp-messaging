using ErrorOr;
using MessageBridge.Application.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Providers;

public sealed class PlaceholderWhatsAppMessageSender(
    IOptions<ProviderOptions> options,
    ILogger<PlaceholderWhatsAppMessageSender> logger)
    : IWhatsAppMessageSender
{
    public Task<ErrorOr<Success>> SendAsync(
        WhatsAppMessage message,
        string tenantId)
    {
        var metadata = options.Value.BuildWhatsAppMetadata(message, tenantId);
        using var _ = logger.BeginScope(metadata);
        logger.LogInformation(
            "Simulating WhatsApp delivery for message {MessageId} through {Provider}.",
            message.MessageId,
            metadata[RecipientMetadataKeys.ProviderKey]);
        return Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private static class RecipientMetadataKeys
    {
        public const string ProviderKey = "provider";
    }
}

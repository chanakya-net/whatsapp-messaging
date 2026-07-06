using ErrorOr;
using MessageBridge.Application.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Providers;

public sealed class PlaceholderEmailConfirmationSender(
    IOptions<ProviderOptions> options,
    ILogger<PlaceholderEmailConfirmationSender> logger)
    : IEmailConfirmationSender
{
    public Task<ErrorOr<Success>> SendAsync(
        EmailConfirmation email,
        string tenantId)
    {
        var metadata = options.Value.BuildEmailMetadata(email, tenantId);
        using var _ = logger.BeginScope(metadata);
        logger.LogInformation(
            "Simulating email confirmation delivery for message {MessageId} through {Provider}.",
            email.MessageId,
            metadata["provider"]);
        return Task.FromResult<ErrorOr<Success>>(new Success());
    }
}

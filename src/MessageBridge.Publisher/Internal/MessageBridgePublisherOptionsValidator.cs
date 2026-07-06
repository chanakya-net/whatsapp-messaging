using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher.Internal;

public sealed class MessageBridgePublisherOptionsValidator : IValidateOptions<MessageBridgePublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, MessageBridgePublisherOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultTenantId))
        {
            return ValidateOptionsResult.Fail("DefaultTenantId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ExchangeName)
            || string.IsNullOrWhiteSpace(options.WhatsAppRoutingKey)
            || string.IsNullOrWhiteSpace(options.EmailRoutingKey))
        {
            return ValidateOptionsResult.Fail("Exchange and routing keys are required.");
        }

        var hasEmptyTenant = options.AllowedTenantIds.Any(t => string.IsNullOrWhiteSpace(t));
        return hasEmptyTenant
            ? ValidateOptionsResult.Fail("AllowedTenantIds cannot contain empty values.")
            : ValidateOptionsResult.Success;
    }
}

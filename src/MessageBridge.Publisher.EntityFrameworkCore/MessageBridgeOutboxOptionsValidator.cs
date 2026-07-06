using MessageBridge.Publisher.Internal;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher.EntityFrameworkCore;

internal sealed class MessageBridgeOutboxOptionsValidator : IValidateOptions<MessageBridgeOutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, MessageBridgeOutboxOptions options)
    {
        if (options.RetryBackoffMultiplier < 1)
        {
            return ValidateOptionsResult.Fail("RetryBackoffMultiplier must be greater than or equal to 1.");
        }

        if (options.CleanupEnabled && options.CleanupRetentionHours < 1)
        {
            return ValidateOptionsResult.Fail("CleanupRetentionHours must be greater than 0 when cleanup is enabled.");
        }

        return ValidateOptionsResult.Success;
    }
}


using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Messaging.Options;

internal sealed class RabbitMqValidateOptions : IValidateOptions<RabbitMqOptions>
{
    private readonly RabbitMqOptionsValidator _validator = new();

    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        var result = _validator.Validate(options);
        if (result.IsValid)
            return ValidateOptionsResult.Success;

        var failures = result.Errors.Select(e => e.ErrorMessage);
        return ValidateOptionsResult.Fail(failures);
    }
}

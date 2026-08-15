using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Messaging.Options;

internal sealed class RabbitMqValidateOptions : IValidateOptions<RabbitMqOptions>
{
    private readonly RabbitMqOptionsValidator _validator;

    public RabbitMqValidateOptions(IHostEnvironment? environment = null)
    {
        _validator = new RabbitMqOptionsValidator(environment?.IsProduction() == true);
    }

    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        var result = _validator.Validate(options);
        if (result.IsValid)
            return ValidateOptionsResult.Success;

        var failures = result.Errors.Select(e => e.ErrorMessage);
        return ValidateOptionsResult.Fail(failures);
    }
}

using FluentValidation;

namespace MessageBridge.Infrastructure.Messaging.Options;

public sealed class RabbitMqOptionsValidator : AbstractValidator<RabbitMqOptions>
{
    public RabbitMqOptionsValidator()
    {
        When(o => o.UsesConnectionString, () =>
        {
            RuleFor(o => o.ConnectionString)
                .Must(s => s!.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase)
                         || s.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase))
                .WithMessage("ConnectionString must begin with amqp:// or amqps://.");
        });

        When(o => !o.UsesConnectionString, () =>
        {
            RuleFor(o => o.Host)
                .NotEmpty()
                .WithMessage("Host is required when ConnectionString is not set.");

            RuleFor(o => o.Username)
                .NotEmpty()
                .WithMessage("Username is required when ConnectionString is not set.");

            RuleFor(o => o.Password)
                .NotEmpty()
                .WithMessage("Password is required when ConnectionString is not set.");

            RuleFor(o => o.Port)
                .GreaterThan((ushort)0)
                .WithMessage("Port must be a valid TCP port (1–65535).");
        });
    }
}

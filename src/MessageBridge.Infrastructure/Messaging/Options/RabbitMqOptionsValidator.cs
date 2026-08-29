using FluentValidation;

namespace MessageBridge.Infrastructure.Messaging.Options;

public sealed class RabbitMqOptionsValidator : AbstractValidator<RabbitMqOptions>
{
    public RabbitMqOptionsValidator(bool requireSecureTransport = false)
    {
        When(o => o.UsesConnectionString, () =>
        {
            RuleFor(o => o.ConnectionString)
                .Must(s => HasAllowedScheme(s!, requireSecureTransport))
                .WithMessage(requireSecureTransport
                    ? "ConnectionString must begin with amqps://."
                    : "ConnectionString must begin with amqp:// or amqps://.");
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

            if (requireSecureTransport)
            {
                RuleFor(o => o.UseSsl)
                    .Equal(true)
                    .WithMessage("UseSsl must be enabled for secure RabbitMQ transport.");
            }
        });
    }

    private static bool HasAllowedScheme(string connectionString, bool requireSecureTransport) =>
        requireSecureTransport
            ? connectionString.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase)
            : connectionString.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase)
              || connectionString.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase);
}

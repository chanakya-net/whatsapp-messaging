using System.Text.RegularExpressions;
using FluentValidation;

namespace MessageBridge.Application.Messages.Validation;

public sealed class SendEmailConfirmationValidator : AbstractValidator<SendEmailConfirmation>
{
    private static readonly Regex TokenUrlBlocker = new(@"(?i)^\s*https?://", RegexOptions.Compiled);

    public SendEmailConfirmationValidator()
    {
        RuleFor(x => x.MessageId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.TenantId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.RecipientEmail)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        RuleFor(x => x.RecipientName)
            .MaximumLength(200)
            .When(x => !string.IsNullOrWhiteSpace(x.RecipientName));

        RuleFor(x => x.ConfirmationToken)
            .NotEmpty()
            .MaximumLength(512)
            .Must(token => !TokenUrlBlocker.IsMatch(token))
            .WithMessage("The confirmation token must not be a URL.");

        RuleFor(x => x.ExpiresAtUtc)
            .NotEmpty()
            .GreaterThan(x => x.RequestedAtUtc)
            .WithMessage("ExpiresAtUtc must be after RequestedAtUtc.");

        RuleFor(x => x.CorrelationId)
            .MaximumLength(128)
            .When(x => !string.IsNullOrWhiteSpace(x.CorrelationId));

        RuleFor(x => x.RequestedAtUtc)
            .NotEmpty()
            .LessThanOrEqualTo(_ => DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage("RequestedAtUtc must not be too far in the future.");
    }
}

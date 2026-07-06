using System.Text.RegularExpressions;
using ErrorOr;
using FluentValidation;
using FluentValidation.Validators;

namespace MessageBridge.Application.Messages.Validation;

public sealed class SendWhatsAppMessageValidator : AbstractValidator<SendWhatsAppMessage>
{
    private static readonly Regex E164PhoneRegex = new(@"^\+[1-9]\d{1,14}$", RegexOptions.Compiled);
    private static readonly Regex Bcp47LanguageRegex = new(@"^[a-zA-Z]{2,8}(-[a-zA-Z0-9]{2,8})*$", RegexOptions.Compiled);

    public SendWhatsAppMessageValidator()
    {
        RuleFor(x => x.MessageId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.TenantId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.RecipientPhoneNumber)
            .NotEmpty()
            .Matches(E164PhoneRegex)
            .WithMessage("The recipient phone number must be a valid E.164 value.");

        RuleFor(x => x.TemplateName)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.TemplateLanguage)
            .NotEmpty()
            .MaximumLength(32)
            .Matches(Bcp47LanguageRegex)
            .WithMessage("The template language must be a valid BCP-47 value.");

        RuleFor(x => x.TemplateParameters)
            .Must(x => x is null or { Count: <= 50 })
            .WithMessage("Template parameters must contain at most 50 entries.");

        RuleForEach(x => x.TemplateParameters!)
            .ChildRules(parameters =>
            {
                parameters.RuleFor(x => x.Key).NotEmpty().MaximumLength(128);
                parameters.RuleFor(x => x.Value).NotNull().MaximumLength(128);
            })
            .When(x => x.TemplateParameters is not null && x.TemplateParameters.Count > 0);

        RuleFor(x => x.CorrelationId)
            .MaximumLength(128)
            .When(x => !string.IsNullOrWhiteSpace(x.CorrelationId));

        RuleFor(x => x.RequestedAtUtc)
            .NotEmpty()
            .LessThanOrEqualTo(_ => DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage("RequestedAtUtc must not be too far in the future.");
    }
}

using FluentValidation;
using MessageBridge.Publisher.Requests;

namespace MessageBridge.Publisher.Validation;

public sealed class SendWhatsAppMessageRequestValidator : AbstractValidator<SendWhatsAppMessageRequest>
{
    public SendWhatsAppMessageRequestValidator()
    {
        RuleFor(x => x.PhoneNumber).NotEmpty().MaximumLength(32);
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.Body).NotEmpty();
        RuleFor(x => x.LanguageCode).NotEmpty();
        RuleFor(x => x.MessageId).MaximumLength(255);
        RuleFor(x => x.CorrelationId).MaximumLength(255);
    }
}

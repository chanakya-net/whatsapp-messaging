using FluentValidation;
using MessageBridge.Publisher.Requests;

namespace MessageBridge.Publisher.Validation;

public sealed class SendEmailConfirmationRequestValidator : AbstractValidator<SendEmailConfirmationRequest>
{
    public SendEmailConfirmationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.ConfirmationCode).NotEmpty();
        RuleFor(x => x.MessageId).MaximumLength(255);
        RuleFor(x => x.CorrelationId).MaximumLength(255);
    }
}

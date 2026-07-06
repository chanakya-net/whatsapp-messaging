using ErrorOr;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Validation;

public sealed class SendWhatsAppMessageValidatorTests
{
    private readonly SendWhatsAppMessageValidator _validator = new();

    [Fact]
    public void Validate_ShouldReturnErrorOrErrors_ForInvalidPayload()
    {
        var command = new SendWhatsAppMessage(
            MessageId: string.Empty,
            TenantId: "tenant-1",
            RecipientPhoneNumber: "12345",
            TemplateName: "template-1",
            TemplateLanguage: "en-US",
            TemplateParameters: null,
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        ValidationResult result = _validator.Validate(command);
        ErrorOr<SendWhatsAppMessage> mappedResult = result.ToErrorOr(command);

        mappedResult.IsError.ShouldBeTrue();
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.MessageId");
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.RecipientPhoneNumber");
    }

    [Fact]
    public void Validate_ShouldReturnCommandForValidPayload()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: new Dictionary<string, string> { ["firstName"] = "Ada" },
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        ValidationResult result = _validator.Validate(command);
        ErrorOr<SendWhatsAppMessage> mappedResult = result.ToErrorOr(command);

        mappedResult.IsError.ShouldBeFalse();
        mappedResult.Value.ShouldBe(command);
    }
}

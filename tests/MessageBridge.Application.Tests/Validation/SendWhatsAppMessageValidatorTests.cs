using ErrorOr;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Validation;

[Trait("Category", "Unit")]
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

    [Fact]
    public void Validate_ShouldAcceptConfiguredBoundaryValues()
    {
        var parameters = Enumerable.Range(1, 50)
            .ToDictionary(index => $"key-{index}", index => $"value-{index}");
        var command = new SendWhatsAppMessage(
            new string('m', 128),
            new string('t', 128),
            "+15551234567",
            new string('n', 128),
            "zh-Hant",
            parameters,
            new string('c', 128),
            DateTimeOffset.UtcNow);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("en_US", "TemplateLanguage")]
    [InlineData("+1555123456789012", "RecipientPhoneNumber")]
    public void Validate_ShouldRejectInvalidFormatBoundaries(string value, string propertyName)
    {
        var command = new SendWhatsAppMessage(
            "msg-001", "tenant-1", "+15551234567", "welcome", "en-US", null, null, DateTimeOffset.UtcNow)
            with
        { TemplateLanguage = value };

        if (propertyName == "RecipientPhoneNumber")
            command = command with { RecipientPhoneNumber = value, TemplateLanguage = "en-US" };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == propertyName);
    }

    [Fact]
    public void Validate_ShouldRejectTooManyParametersAndFutureRequest()
    {
        var parameters = Enumerable.Range(1, 51)
            .ToDictionary(index => $"key-{index}", index => "value");
        var command = new SendWhatsAppMessage(
            "msg-001", "tenant-1", "+15551234567", "welcome", "en-US", parameters, null,
            DateTimeOffset.UtcNow.AddMinutes(6));

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == "TemplateParameters");
        result.Errors.ShouldContain(error => error.PropertyName == "RequestedAtUtc");
    }

    [Fact]
    public async Task ValidateAsync_ShouldThrowOperationCanceledException_WhenCancellationTokenIsCancelled()
    {
        var command = new SendWhatsAppMessage(
            "msg-001", "tenant-1", "+15551234567", "welcome", "en-US", null, null, DateTimeOffset.UtcNow);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _validator.ValidateAsync(command, cts.Token));
    }
}

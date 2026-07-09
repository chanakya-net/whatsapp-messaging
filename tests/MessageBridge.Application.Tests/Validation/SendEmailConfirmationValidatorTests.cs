using ErrorOr;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Validation;

public sealed class SendEmailConfirmationValidatorTests
{
    private readonly SendEmailConfirmationValidator _validator = new();

    [Fact]
    public void Validate_ShouldReturnErrorOrErrors_ForInvalidPayload()
    {
        var command = new SendEmailConfirmation(
            MessageId: string.Empty,
            TenantId: "tenant-1",
            RecipientEmail: "not-an-email",
            RecipientName: null,
            ConfirmationToken: "https://example.com/confirm?token=abc",
            ExpiresAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        ValidationResult result = _validator.Validate(command);
        ErrorOr<SendEmailConfirmation> mappedResult = result.ToErrorOr(command);

        mappedResult.IsError.ShouldBeTrue();
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.MessageId");
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.RecipientEmail");
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.ConfirmationToken");
        mappedResult.Errors.ShouldContain(error => error.Code == "Validation.RequestedAtUtc");
    }

    [Fact]
    public void Validate_ShouldReturnCommandForValidPayload()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientEmail: "user@example.com",
            RecipientName: "Ada Lovelace",
            ConfirmationToken: "abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        ValidationResult result = _validator.Validate(command);
        ErrorOr<SendEmailConfirmation> mappedResult = result.ToErrorOr(command);

        mappedResult.IsError.ShouldBeFalse();
        mappedResult.Value.ShouldBe(command);
    }
}

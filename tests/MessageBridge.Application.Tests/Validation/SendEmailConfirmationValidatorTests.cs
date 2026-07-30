using ErrorOr;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Validation;

[Trait("Category", "Unit")]
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

    [Fact]
    public void Validate_ShouldAcceptConfiguredBoundaryValues()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendEmailConfirmation(
            new string('m', 128),
            new string('t', 128),
            $"{new string('a', 314)}@x.com",
            new string('n', 200),
            new string('k', 512),
            requestedAt.AddMinutes(1),
            new string('c', 128),
            requestedAt);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectExpirationAtOrBeforeRequest()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendEmailConfirmation(
            "msg-001", "tenant-1", "user@example.com", null, "token-abc123", requestedAt, null, requestedAt);

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == "ExpiresAtUtc");
    }

    [Theory]
    [InlineData("https://example.com/token", "ConfirmationToken")]
    [InlineData("user example", "RecipientEmail")]
    public void Validate_ShouldRejectUnsafeBoundaryValues(string value, string propertyName)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendEmailConfirmation(
            "msg-001", "tenant-1", "user@example.com", null, "token-abc123", requestedAt.AddMinutes(1), null, requestedAt)
            with { ConfirmationToken = value };

        if (propertyName == "RecipientEmail")
            command = command with { RecipientEmail = value, ConfirmationToken = "token-abc123" };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == propertyName);
    }

    [Fact]
    public async Task ValidateAsync_ShouldThrowOperationCanceledException_WhenCancellationTokenIsCancelled()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendEmailConfirmation(
            "msg-001", "tenant-1", "user@example.com", null, "token-abc123", requestedAt.AddMinutes(1), null, requestedAt);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _validator.ValidateAsync(command, cts.Token));
    }
}

using MessageBridge.Application.Providers;
using MessageBridge.Domain.Privacy;
using MessageBridge.Infrastructure.Providers;
using MessageBridge.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.Providers;

public sealed class PlaceholderEmailConfirmationSenderTests
{
    [Fact]
    public async Task SendAsync_returns_success_and_logs_deterministic_metadata()
    {
        var options = Options.Create(new ProviderOptions());
        var logger = new ProviderTestLogger<PlaceholderEmailConfirmationSender>();
        var sender = new PlaceholderEmailConfirmationSender(options, logger);
        var message = new EmailConfirmation(
            MessageId: "msg-email-001",
            RecipientEmailAddress: "user@example.com",
            TemplateName: "confirm-email",
            ConfirmationToken: "token-xyz",
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var result = await sender.SendAsync(message, "tenant-1");

        result.IsError.ShouldBeFalse();
        logger.Scopes.ShouldHaveSingleItem();
        var metadata = logger.Scopes[0];
        var expectedProvider = options.Value.EmailProviderName;

        metadata["provider"].ShouldBe(expectedProvider);
        metadata["message_id"].ShouldBe("msg-email-001");
        metadata["template_name"].ShouldBe("confirm-email");
        metadata["tenant_id"].ShouldBe("tenant-1");
        metadata["recipient_masked"].ShouldBe(RecipientMasker.MaskEmailAddress("user@example.com"));

        var firstLog = logger.Messages.ShouldHaveSingleItem();
        firstLog.ShouldNotContain("user@example.com");
        firstLog.ShouldNotContain("token-xyz");
    }

    [Fact]
    public void BuildEmailMetadata_is_deterministic()
    {
        var options = new ProviderOptions();
        var message = new EmailConfirmation(
            MessageId: "msg-email-001",
            RecipientEmailAddress: "user@example.com",
            TemplateName: "confirm-email",
            ConfirmationToken: "token-xyz",
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);
        var first = options.BuildEmailMetadata(message, "tenant-1");
        var second = options.BuildEmailMetadata(message, "tenant-1");

        first["provider_message_id"].ShouldBe(second["provider_message_id"]);
        first["recipient_masked"].ShouldBe(second["recipient_masked"]);
        first["template_name"].ShouldBe(second["template_name"]);
    }
}

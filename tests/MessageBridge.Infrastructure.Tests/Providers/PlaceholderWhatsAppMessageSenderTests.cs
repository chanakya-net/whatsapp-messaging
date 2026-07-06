using MessageBridge.Application.Providers;
using MessageBridge.Domain.Privacy;
using MessageBridge.Infrastructure.Providers;
using MessageBridge.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.Providers;

public sealed class PlaceholderWhatsAppMessageSenderTests
{
    [Fact]
    public async Task SendAsync_returns_success_and_logs_deterministic_metadata()
    {
        var options = Options.Create(new ProviderOptions());
        var logger = new ProviderTestLogger<PlaceholderWhatsAppMessageSender>();
        var sender = new PlaceholderWhatsAppMessageSender(options, logger);
        var message = new WhatsAppMessage(
            MessageId: "msg-001",
            RecipientPhoneNumber: "+1 (555) 123-4567",
            TemplateName: "welcome",
            TemplateLanguage: "en",
            TemplateParameters: new Dictionary<string, string> { ["plan"] = "pro" },
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var result = await sender.SendAsync(message, "tenant-1");

        result.IsError.ShouldBeFalse();
        logger.Scopes.ShouldHaveSingleItem();
        var metadata = logger.Scopes[0];
        var expectedProvider = options.Value.WhatsAppProviderName;

        metadata["provider"].ShouldBe(expectedProvider);
        metadata["message_id"].ShouldBe("msg-001");
        metadata["template_name"].ShouldBe("welcome");
        metadata["tenant_id"].ShouldBe("tenant-1");
        metadata["recipient_masked"].ShouldBe(RecipientMasker.MaskPhoneNumber("+1 (555) 123-4567"));
        metadata["template_parameters_count"].ShouldBe("1");

        var firstLog = logger.Messages.ShouldHaveSingleItem();
        firstLog.ShouldNotContain("+1 (555) 123-4567");
        firstLog.ShouldNotContain("plan");
        firstLog.ShouldContain("msg-001");
    }

    [Fact]
    public void BuildWhatsAppMetadata_is_deterministic()
    {
        var options = new ProviderOptions();
        var message = new WhatsAppMessage(
            MessageId: "msg-001",
            RecipientPhoneNumber: "+1 (555) 123-4567",
            TemplateName: "welcome",
            TemplateLanguage: "en",
            TemplateParameters: new Dictionary<string, string> { ["name"] = "Ada", ["plan"] = "pro" },
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);
        var first = options.BuildWhatsAppMetadata(message, "tenant-1");
        var second = options.BuildWhatsAppMetadata(message, "tenant-1");

        first["provider_message_id"].ShouldBe(second["provider_message_id"]);
        first["recipient_masked"].ShouldBe(second["recipient_masked"]);
        first["template_parameters_count"].ShouldBe(second["template_parameters_count"]);
    }
}

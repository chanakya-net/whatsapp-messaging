using MessageBridge.Application.Providers;
using MessageBridge.Domain.Privacy;
using MessageBridge.Infrastructure.Providers;
using MessageBridge.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.Providers;

[Trait("Category", "Unit")]
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
        metadata["delivery_status"].ShouldBe("simulated");
        metadata["message_id"].ShouldBe("msg-001");
        metadata["template_name"].ShouldBe("welcome");
        metadata["tenant_id"].ShouldBe("tenant-1");
        metadata["recipient_masked"].ShouldBe(RecipientMasker.MaskPhoneNumber("+1 (555) 123-4567"));
        metadata["template_parameters_count"].ShouldBe("1");

        var firstLog = logger.Messages.ShouldHaveSingleItem();
        firstLog.ShouldNotContain("+1 (555) 123-4567");
        firstLog.ShouldNotContain("plan");
        firstLog.ShouldContain("msg-001");
        firstLog.ShouldContain("simulated");
        metadata.Values.Any(value => value?.ToString() == "+1 (555) 123-4567").ShouldBeFalse();
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

    [Fact]
    public async Task SendAsync_logs_masked_phone_on_success()
    {
        var options = Options.Create(new ProviderOptions());
        var logger = new ProviderTestLogger<PlaceholderWhatsAppMessageSender>();
        var sender = new PlaceholderWhatsAppMessageSender(options, logger);
        var phone = "+1 (555) 987-6543";
        var message = new WhatsAppMessage(
            MessageId: "msg-phone-mask",
            RecipientPhoneNumber: phone,
            TemplateName: "reminder",
            TemplateLanguage: "en",
            TemplateParameters: null,
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        await sender.SendAsync(message, "tenant-2");

        logger.Scopes[0]["recipient_masked"].ShouldBe(RecipientMasker.MaskPhoneNumber(phone));
        logger.Messages[0].ShouldNotContain(phone);
    }

    [Fact]
    public async Task SendAsync_with_empty_parameters_includes_zero_count()
    {
        var options = Options.Create(new ProviderOptions());
        var logger = new ProviderTestLogger<PlaceholderWhatsAppMessageSender>();
        var sender = new PlaceholderWhatsAppMessageSender(options, logger);
        var message = new WhatsAppMessage(
            MessageId: "msg-no-params",
            RecipientPhoneNumber: "+15551111111",
            TemplateName: "generic",
            TemplateLanguage: "en",
            TemplateParameters: new Dictionary<string, string>(),
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        await sender.SendAsync(message, "tenant-1");

        logger.Scopes[0]["template_parameters_count"].ShouldBe("0");
    }

    [Fact]
    public void SendAsync_propagates_provider_failure_without_logging_sensitive_data()
    {
        var logger = new ProviderTestLogger<PlaceholderWhatsAppMessageSender>();
        var sender = new PlaceholderWhatsAppMessageSender(
            new ThrowingOptions<ProviderOptions>(new InvalidOperationException("provider unavailable")),
            logger);

        var exception = Should.Throw<InvalidOperationException>(() =>
            sender.SendAsync(CreateMessage("failure", "+15551234567"), "tenant-1"));

        exception.Message.ShouldBe("provider unavailable");
        logger.Scopes.ShouldBeEmpty();
        logger.Messages.ShouldBeEmpty();
    }

    [Fact]
    public void SendAsync_propagates_cancellation_without_logging_sensitive_data()
    {
        var logger = new ProviderTestLogger<PlaceholderWhatsAppMessageSender>();
        var sender = new PlaceholderWhatsAppMessageSender(
            new ThrowingOptions<ProviderOptions>(new OperationCanceledException("cancelled")),
            logger);

        Should.Throw<OperationCanceledException>(() =>
            sender.SendAsync(CreateMessage("cancelled", "+15551234567"), "tenant-1"));

        logger.Scopes.ShouldBeEmpty();
        logger.Messages.ShouldBeEmpty();
    }

    private static WhatsAppMessage CreateMessage(string messageId, string phone) => new(
        MessageId: messageId,
        RecipientPhoneNumber: phone,
        TemplateName: "welcome",
        TemplateLanguage: "en",
        TemplateParameters: null,
        CorrelationId: null,
        RequestedAtUtc: DateTimeOffset.UtcNow);

    private sealed class ThrowingOptions<T>(Exception exception) : IOptions<T>
        where T : class
    {
        public T Value => throw exception;
    }
}

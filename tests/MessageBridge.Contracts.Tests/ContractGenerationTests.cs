using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MessageBridge.Contracts.V1;

namespace MessageBridge.Contracts.Tests;

public class ContractGenerationTests
{
    [Fact]
    public void WhatsAppCommand_CanInstantiate()
    {
        var cmd = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-123",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { { "first_name", "Alex" } },
            CorrelationId = "corr-456",
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        Assert.NotNull(cmd);
        Assert.Equal("msg-123", cmd.MessageId);
        Assert.Equal("Alex", cmd.TemplateParameters["first_name"]);
    }

    [Fact]
    public void WhatsAppCommand_CanSerializeDeserialize()
    {
        var original = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-123",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { { "first_name", "Alex" }, { "code", "123" } },
            CorrelationId = "corr-456",
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        var serialized = original.ToByteArray();
        var deserialized = SendWhatsAppMessageCommand.ParseFrom(serialized);

        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.TenantId, deserialized.TenantId);
        Assert.Equal(original.RecipientPhoneNumber, deserialized.RecipientPhoneNumber);
        Assert.Equal(original.TemplateName, deserialized.TemplateName);
        Assert.Equal(original.TemplateLanguage, deserialized.TemplateLanguage);
        Assert.Equal(original.TemplateParameters["first_name"], deserialized.TemplateParameters["first_name"]);
        Assert.Equal(original.TemplateParameters["code"], deserialized.TemplateParameters["code"]);
        Assert.Equal(original.CorrelationId, deserialized.CorrelationId);
        Assert.NotNull(deserialized.RequestedAtUtc);
        Assert.Equal(original.RequestedAtUtc.Seconds, deserialized.RequestedAtUtc.Seconds);
    }

    [Fact]
    public void EmailCommand_CanInstantiate()
    {
        var cmd = new SendEmailConfirmationCommand
        {
            MessageId = "email-123",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = "John Doe",
            ConfirmationToken = "token-abc123xyz",
            CorrelationId = "corr-789",
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            ExpiresAtUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(24))
        };

        Assert.NotNull(cmd);
        Assert.Equal("email-123", cmd.MessageId);
    }

    [Fact]
    public void EmailCommand_CanSerializeDeserialize()
    {
        var now = DateTime.UtcNow;
        var expires = now.AddHours(24);

        var original = new SendEmailConfirmationCommand
        {
            MessageId = "email-123",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = "John Doe",
            ConfirmationToken = "token-abc123xyz",
            CorrelationId = "corr-789",
            RequestedAtUtc = Timestamp.FromDateTime(now),
            ExpiresAtUtc = Timestamp.FromDateTime(expires)
        };

        var serialized = original.ToByteArray();
        var deserialized = SendEmailConfirmationCommand.ParseFrom(serialized);

        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.TenantId, deserialized.TenantId);
        Assert.Equal(original.RecipientEmail, deserialized.RecipientEmail);
        Assert.Equal(original.RecipientName, deserialized.RecipientName);
        Assert.Equal(original.ConfirmationToken, deserialized.ConfirmationToken);
        Assert.Equal(original.CorrelationId, deserialized.CorrelationId);
    }
}

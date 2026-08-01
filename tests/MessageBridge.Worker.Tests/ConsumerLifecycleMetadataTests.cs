using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Consumers;
using Shouldly;
using Xunit;

namespace MessageBridge.Worker.Tests;

[Trait("Category", "Unit")]
public sealed class ConsumerLifecycleMetadataTests
{
    [Fact]
    public void ForWhatsApp_Contains_All_Required_Keys()
    {
        var message = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-001",
            TenantId = "tenant-1",
            TemplateName = "welcome",
            RecipientPhoneNumber = "+1 (555) 123-4567",
            TemplateLanguage = "en"
        };

        var metadata = ConsumerLifecycleMetadata.ForWhatsApp(message);

        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.MessageIdKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.TenantIdKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.TemplateNameKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.RecipientKey);
        metadata.Keys.ShouldNotContain("TemplateParameters");
    }

    [Fact]
    public void ForEmailConfirmation_Contains_All_Required_Keys()
    {
        var message = new SendEmailConfirmationCommand
        {
            MessageId = "msg-001",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "token-123"
        };

        var metadata = ConsumerLifecycleMetadata.ForEmailConfirmation(message);

        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.MessageIdKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.TenantIdKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.TemplateNameKey);
        metadata.Keys.ShouldContain(ConsumerLifecycleMetadata.RecipientKey);
        metadata[ConsumerLifecycleMetadata.TemplateNameKey].ShouldBe("confirm-email");
    }

    [Fact]
    public void ForWhatsApp_Masks_Phone_Consistently()
    {
        var message1 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-1",
            TenantId = "tenant-1",
            TemplateName = "test",
            RecipientPhoneNumber = "+14155552671",
            TemplateLanguage = "en"
        };
        var message2 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-2",
            TenantId = "tenant-1",
            TemplateName = "test",
            RecipientPhoneNumber = "+14155552671",
            TemplateLanguage = "en"
        };

        var metadata1 = ConsumerLifecycleMetadata.ForWhatsApp(message1);
        var metadata2 = ConsumerLifecycleMetadata.ForWhatsApp(message2);

        metadata1[ConsumerLifecycleMetadata.RecipientKey].ShouldBe(metadata2[ConsumerLifecycleMetadata.RecipientKey]);
    }

    [Fact]
    public void ForEmailConfirmation_Masks_Email_Consistently()
    {
        var message1 = new SendEmailConfirmationCommand
        {
            MessageId = "msg-1",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "token"
        };
        var message2 = new SendEmailConfirmationCommand
        {
            MessageId = "msg-2",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "token"
        };

        var metadata1 = ConsumerLifecycleMetadata.ForEmailConfirmation(message1);
        var metadata2 = ConsumerLifecycleMetadata.ForEmailConfirmation(message2);

        metadata1[ConsumerLifecycleMetadata.RecipientKey].ShouldBe(metadata2[ConsumerLifecycleMetadata.RecipientKey]);
    }

    [Fact]
    public void ForWhatsApp_Different_Phones_Produce_Different_Masks()
    {
        var msg1 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-1",
            TenantId = "tenant-1",
            TemplateName = "test",
            RecipientPhoneNumber = "+14155552671",
            TemplateLanguage = "en"
        };
        var msg2 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-2",
            TenantId = "tenant-1",
            TemplateName = "test",
            RecipientPhoneNumber = "+441234567890",
            TemplateLanguage = "en"
        };

        var meta1 = ConsumerLifecycleMetadata.ForWhatsApp(msg1);
        var meta2 = ConsumerLifecycleMetadata.ForWhatsApp(msg2);

        meta1[ConsumerLifecycleMetadata.RecipientKey].ShouldNotBe(meta2[ConsumerLifecycleMetadata.RecipientKey]);
    }
}

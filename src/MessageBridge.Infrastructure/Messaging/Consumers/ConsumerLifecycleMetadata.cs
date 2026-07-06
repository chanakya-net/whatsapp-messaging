using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Privacy;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public static class ConsumerLifecycleMetadata
{
    public const string MessageIdKey = "message_id";
    public const string TenantIdKey = "tenant_id";
    public const string TemplateNameKey = "template_name";
    public const string RecipientKey = "recipient_masked";
    public const string CorrelationIdKey = "correlation_id";

    public static IReadOnlyDictionary<string, object?> ForWhatsApp(SendWhatsAppMessageCommand message)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessageIdKey] = message.MessageId,
            [TenantIdKey] = message.TenantId,
            [TemplateNameKey] = message.TemplateName,
            [RecipientKey] = RecipientMasker.MaskPhoneNumber(message.RecipientPhoneNumber),
            [CorrelationIdKey] = string.IsNullOrWhiteSpace(message.CorrelationId) ? null : message.CorrelationId
        };
    }

    public static IReadOnlyDictionary<string, object?> ForEmailConfirmation(SendEmailConfirmationCommand message)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessageIdKey] = message.MessageId,
            [TenantIdKey] = message.TenantId,
            [TemplateNameKey] = "confirm-email",
            [RecipientKey] = RecipientMasker.MaskEmailAddress(message.RecipientEmail),
            [CorrelationIdKey] = string.IsNullOrWhiteSpace(message.CorrelationId) ? null : message.CorrelationId
        };
    }
}

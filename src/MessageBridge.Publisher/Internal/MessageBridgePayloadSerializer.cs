using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MessageBridge.Contracts.V1;
using MessageBridge.Publisher.Requests;

namespace MessageBridge.Publisher.Internal;

internal static class MessageBridgePayloadSerializer
{
    public static byte[] SerializeWhatsApp(SendWhatsAppMessageRequest request)
    {
        var payload = new SendWhatsAppMessageCommand
        {
            MessageId = request.MessageId ?? string.Empty,
            TenantId = request.TenantId ?? string.Empty,
            RecipientPhoneNumber = request.PhoneNumber,
            TemplateName = request.TemplateId,
            TemplateLanguage = request.LanguageCode,
            CorrelationId = request.CorrelationId ?? string.Empty,
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        payload.TemplateParameters.Add("body", request.Body);

        return payload.ToByteArray();
    }

    public static byte[] SerializeEmail(SendEmailConfirmationRequest request)
    {
        var payload = new SendEmailConfirmationCommand
        {
            MessageId = request.MessageId ?? string.Empty,
            TenantId = request.TenantId ?? string.Empty,
            RecipientEmail = request.Email,
            ConfirmationToken = request.ConfirmationCode,
            CorrelationId = request.CorrelationId ?? string.Empty,
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        return payload.ToByteArray();
    }
}

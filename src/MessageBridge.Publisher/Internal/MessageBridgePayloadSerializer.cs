using System.IO;
using MessageBridge.Publisher.Internal.Contracts;
using MessageBridge.Publisher.Requests;
using ProtoBuf;

namespace MessageBridge.Publisher.Internal;

internal static class MessageBridgePayloadSerializer
{
    public static byte[] SerializeWhatsApp(SendWhatsAppMessageRequest request)
    {
        var payload = new SendWhatsAppMessageContract
        {
            TenantId = request.TenantId ?? string.Empty,
            MessageId = request.MessageId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            PhoneNumber = request.PhoneNumber,
            TemplateId = request.TemplateId,
            Body = request.Body,
            LanguageCode = request.LanguageCode,
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, payload);
        return stream.ToArray();
    }

    public static byte[] SerializeEmail(SendEmailConfirmationRequest request)
    {
        var payload = new SendEmailConfirmationContract
        {
            TenantId = request.TenantId ?? string.Empty,
            MessageId = request.MessageId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            Email = request.Email,
            ConfirmationCode = request.ConfirmationCode,
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, payload);
        return stream.ToArray();
    }
}

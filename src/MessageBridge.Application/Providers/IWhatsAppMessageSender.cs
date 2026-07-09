using ErrorOr;

namespace MessageBridge.Application.Providers;

public interface IWhatsAppMessageSender
{
    Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId);
}

public sealed record WhatsAppMessage(
    string MessageId,
    string RecipientPhoneNumber,
    string TemplateName,
    string TemplateLanguage,
    IReadOnlyDictionary<string, string>? TemplateParameters,
    string? CorrelationId,
    DateTimeOffset RequestedAtUtc);

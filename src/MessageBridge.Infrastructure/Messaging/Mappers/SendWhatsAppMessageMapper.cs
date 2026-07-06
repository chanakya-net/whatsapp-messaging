using MessageBridge.Application.Messages;
using MessageBridge.Contracts.V1;
using Google.Protobuf.WellKnownTypes;

namespace MessageBridge.Infrastructure.Messaging.Mappers;

public static class SendWhatsAppMessageMapper
{
    public static SendWhatsAppMessage ToApplicationCommand(this SendWhatsAppMessageCommand contract)
    {
        return new SendWhatsAppMessage(
            MessageId: contract.MessageId,
            TenantId: contract.TenantId,
            RecipientPhoneNumber: contract.RecipientPhoneNumber,
            TemplateName: contract.TemplateName,
            TemplateLanguage: contract.TemplateLanguage,
            TemplateParameters: NormalizeParameters(contract),
            CorrelationId: NormalizeNullable(contract.CorrelationId),
            RequestedAtUtc: contract.RequestedAtUtc.ToDateTimeOffset());
    }

    private static IReadOnlyDictionary<string, string>? NormalizeParameters(
        SendWhatsAppMessageCommand contract) =>
        contract.TemplateParameters.Count == 0
            ? null
            : contract.TemplateParameters.ToDictionary(x => x.Key, x => x.Value);

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

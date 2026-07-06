using MessageBridge.Application.Messages;
using MessageBridge.Contracts.V1;
using Google.Protobuf.WellKnownTypes;

namespace MessageBridge.Infrastructure.Messaging.Mappers;

public static class SendEmailConfirmationMapper
{
    public static SendEmailConfirmation ToApplicationCommand(this SendEmailConfirmationCommand contract)
    {
        return new SendEmailConfirmation(
            MessageId: contract.MessageId,
            TenantId: contract.TenantId,
            RecipientEmail: contract.RecipientEmail,
            RecipientName: NormalizeNullable(contract.RecipientName),
            ConfirmationToken: contract.ConfirmationToken,
            ExpiresAtUtc: contract.ExpiresAtUtc.ToDateTimeOffset(),
            CorrelationId: NormalizeNullable(contract.CorrelationId),
            RequestedAtUtc: contract.RequestedAtUtc.ToDateTimeOffset());
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

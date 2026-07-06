using ErrorOr;

namespace MessageBridge.Application.Providers;

public interface IEmailConfirmationSender
{
    Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId);
}

public sealed record EmailConfirmation(
    string MessageId,
    string RecipientEmailAddress,
    string TemplateName,
    string ConfirmationToken,
    string? CorrelationId,
    DateTimeOffset RequestedAtUtc);

using System;

namespace MessageBridge.Application.Messages;

public sealed record SendEmailConfirmation(
    string MessageId,
    string TenantId,
    string RecipientEmail,
    string? RecipientName,
    string ConfirmationToken,
    DateTimeOffset ExpiresAtUtc,
    string? CorrelationId,
    DateTimeOffset RequestedAtUtc);

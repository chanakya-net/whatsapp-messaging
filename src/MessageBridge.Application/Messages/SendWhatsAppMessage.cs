using System;
using System.Collections.Generic;

namespace MessageBridge.Application.Messages;

public sealed record SendWhatsAppMessage(
    string MessageId,
    string TenantId,
    string RecipientPhoneNumber,
    string TemplateName,
    string TemplateLanguage,
    IReadOnlyDictionary<string, string>? TemplateParameters,
    string? CorrelationId,
    DateTimeOffset RequestedAtUtc);

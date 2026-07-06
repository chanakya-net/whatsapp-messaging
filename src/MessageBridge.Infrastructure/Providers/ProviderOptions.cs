using System.ComponentModel.DataAnnotations;
using MessageBridge.Application.Providers;
using MessageBridge.Domain.Privacy;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Providers;

public sealed class ProviderOptions
{
    public const string SectionName = "MessageBridge:Providers";

    [Required]
    public string WhatsAppProviderName { get; set; } = "placeholder-whatsapp";

    [Required]
    public string EmailProviderName { get; set; } = "placeholder-email";

    public IReadOnlyDictionary<string, string?> BuildWhatsAppMetadata(
        WhatsAppMessage message,
        string tenantId)
    {
        var metadata = BuildMetadata(
            Provider: WhatsAppProviderName,
            MessageId: message.MessageId,
            TenantId: tenantId,
            TemplateName: message.TemplateName,
            RecipientMasked: RecipientMasker.MaskPhoneNumber(message.RecipientPhoneNumber));

        metadata["template_parameters_count"] = (message.TemplateParameters?.Count ?? 0).ToString();
        return metadata;
    }

    public IReadOnlyDictionary<string, string?> BuildEmailMetadata(
        EmailConfirmation email,
        string tenantId) =>
        BuildMetadata(
            Provider: EmailProviderName,
            MessageId: email.MessageId,
            TenantId: tenantId,
            TemplateName: "confirm-email",
            RecipientMasked: RecipientMasker.MaskEmailAddress(email.RecipientEmailAddress));

    private static Dictionary<string, string?> BuildMetadata(
        string Provider,
        string MessageId,
        string TenantId,
        string TemplateName,
        string RecipientMasked)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["provider_message_id"] = $"{Provider}:{MessageId}",
            ["message_id"] = MessageId,
            ["tenant_id"] = TenantId,
            ["template_name"] = TemplateName,
            ["recipient_masked"] = RecipientMasked
        };
    }
}

public sealed class ProviderOptionsValidator : IValidateOptions<ProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.WhatsAppProviderName))
            failures.Add($"{nameof(ProviderOptions.WhatsAppProviderName)} is required.");

        if (string.IsNullOrWhiteSpace(options.EmailProviderName))
            failures.Add($"{nameof(ProviderOptions.EmailProviderName)} is required.");

        return failures.Count is 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

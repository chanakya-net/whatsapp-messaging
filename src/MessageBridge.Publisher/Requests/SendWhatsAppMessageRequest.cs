namespace MessageBridge.Publisher.Requests;

public sealed class SendWhatsAppMessageRequest
{
    public string? TenantId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "en-US";
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
}

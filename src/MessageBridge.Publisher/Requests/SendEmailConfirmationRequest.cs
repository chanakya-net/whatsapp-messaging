namespace MessageBridge.Publisher.Requests;

public sealed class SendEmailConfirmationRequest
{
    public string? TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ConfirmationCode { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
}

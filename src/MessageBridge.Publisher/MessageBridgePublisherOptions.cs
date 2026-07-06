using System.ComponentModel.DataAnnotations;

namespace MessageBridge.Publisher;

public sealed class MessageBridgePublisherOptions
{
    [Required]
    public string DefaultTenantId { get; set; } = string.Empty;

    public IReadOnlyCollection<string> AllowedTenantIds { get; set; } = Array.Empty<string>();

    [Required]
    public string ExchangeName { get; set; } = "messagebridge.commands";

    [Required]
    public string WhatsAppRoutingKey { get; set; } = "whatsapp.send";

    [Required]
    public string EmailRoutingKey { get; set; } = "email.confirmation";
}

namespace MessageBridge.Publisher.EntityFrameworkCore.Outbox;

public sealed class MessageBridgeOutboxMessage
{
    public string Id { get; set; } = null!;
    public string MessageId { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string ExchangeName { get; set; } = null!;
    public string RoutingKey { get; set; } = null!;
    public string Headers { get; set; } = null!;
    public byte[] Payload { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}

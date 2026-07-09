namespace MessageBridge.Publisher;

public sealed record MessageBridgePublisherResult(string MessageId, string CorrelationId, string TenantId);

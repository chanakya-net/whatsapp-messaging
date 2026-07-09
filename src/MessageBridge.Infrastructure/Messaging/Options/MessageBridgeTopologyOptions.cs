namespace MessageBridge.Infrastructure.Messaging.Options;

/// <summary>
/// Topology naming rules for exchanges, queues, and routing keys.
/// Names are prefixed with EnvironmentPrefix when set (e.g., "prod", "staging").
/// </summary>
public sealed class MessageBridgeTopologyOptions
{
    public const string SectionName = "MessageBridge:Topology";

    /// <summary>
    /// Short environment label prepended to exchange and queue names.
    /// Leave empty for single-environment deployments.
    /// </summary>
    public string EnvironmentPrefix { get; set; } = string.Empty;

    /// <summary>Computes the durable topic exchange name for a given base name.</summary>
    public string ExchangeName(string baseName) => ApplyPrefix(baseName);

    /// <summary>Computes the durable queue name for a given base name.</summary>
    public string QueueName(string baseName) => ApplyPrefix(baseName);

    /// <summary>
    /// Returns a stable, lowercase routing key for a message type name.
    /// Routing keys are never prefixed so consumers across environments can bind selectively.
    /// </summary>
    public string RoutingKey(string messageType) =>
        string.IsNullOrWhiteSpace(messageType)
            ? throw new ArgumentException("messageType must not be empty.", nameof(messageType))
            : messageType.ToLowerInvariant();

    private string ApplyPrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name must not be empty.", nameof(name));

        return string.IsNullOrWhiteSpace(EnvironmentPrefix)
            ? name
            : $"{EnvironmentPrefix}.{name}";
    }
}

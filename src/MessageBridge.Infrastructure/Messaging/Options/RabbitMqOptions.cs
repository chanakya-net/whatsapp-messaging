namespace MessageBridge.Infrastructure.Messaging.Options;

/// <summary>
/// RabbitMQ / CloudAMQP connection options.
/// ConnectionString takes precedence over decomposed fields when both are provided.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// AMQP connection URI (amqp:// or amqps://). Overrides all decomposed fields.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Hostname or IP; used when ConnectionString is absent.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>AMQP port; defaults to 5672 (plain) or 5671 (TLS).</summary>
    public ushort Port { get; set; } = 5672;

    /// <summary>RabbitMQ virtual host; defaults to "/".</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Username for decomposed connection.</summary>
    public string? Username { get; set; }

    /// <summary>Password for decomposed connection. Never log this value.</summary>
    public string? Password { get; set; }

    /// <summary>Enables TLS for decomposed connections.</summary>
    public bool UseSsl { get; set; }

    /// <summary>True when a connection URI is configured and takes precedence.</summary>
    public bool UsesConnectionString => !string.IsNullOrWhiteSpace(ConnectionString);
}

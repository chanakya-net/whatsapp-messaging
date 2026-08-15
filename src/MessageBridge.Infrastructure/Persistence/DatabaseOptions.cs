namespace MessageBridge.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public bool UseEntraAuth { get; set; }

    public int MaxPoolSize { get; set; } = 12;

    public string? ManagedIdentityClientId { get; set; }
}

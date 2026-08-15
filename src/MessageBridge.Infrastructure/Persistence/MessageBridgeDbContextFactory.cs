using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageBridgeDbContextFactory : IDesignTimeDbContextFactory<MessageBridgeDbContext>
{
    public MessageBridgeDbContext CreateDbContext(string[] args)
    {
        var dataSource = NpgsqlDataSourceFactory.Create(ReadOptions());
        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(dataSource)
            .Options;

        return new MessageBridgeDbContext(options);
    }

    private static DatabaseOptions ReadOptions()
    {
        var useEntraAuth = ReadBool("UseEntraAuth", false);
        return new DatabaseOptions
        {
            Host = Read("Host", "localhost"),
            Port = ReadInt("Port", 5432),
            Database = Read("Database", "messagebridge_dev"),
            Username = Read("Username", "dev"),
            Password = Environment.GetEnvironmentVariable("Database__Password")
                ?? (useEntraAuth ? null : "dev"),
            UseEntraAuth = useEntraAuth,
            MaxPoolSize = ReadInt("MaxPoolSize", 12),
            ManagedIdentityClientId = Environment.GetEnvironmentVariable(
                "Database__ManagedIdentityClientId")
        };
    }

    private static string Read(string name, string fallback)
        => Environment.GetEnvironmentVariable($"Database__{name}") ?? fallback;

    private static int ReadInt(string name, int fallback)
    {
        var configured = Environment.GetEnvironmentVariable($"Database__{name}");
        return configured is null ? fallback : int.Parse(configured);
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var configured = Environment.GetEnvironmentVariable($"Database__{name}");
        return configured is null ? fallback : bool.Parse(configured);
    }
}

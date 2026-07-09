using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageBridgeDbContextFactory : IDesignTimeDbContextFactory<MessageBridgeDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=messagebridge;Username=postgres;Password=postgres";

    public MessageBridgeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MESSAGEBRIDGE_CONNECTION_STRING")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new MessageBridgeDbContext(options);
    }
}

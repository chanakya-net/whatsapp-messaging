using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MessageBridge.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<MessageBridgeDbContext> CreateDbContextAsync()
    {
        var connBuilder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = $"messagebridge_tests_{Guid.NewGuid():N}"
        };

        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connBuilder.ConnectionString)
            .Options;

        var dbContext = new MessageBridgeDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return dbContext;
    }
}

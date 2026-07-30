using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace MessageBridge.IntegrationTests.Fixtures;

/// <summary>
/// Owns one shared PostgreSQL and one shared RabbitMQ container for the whole
/// integration test collection. Individual tests get an isolated database via
/// <see cref="CreateMigratedDatabaseAsync"/> and an isolated topology prefix via
/// <see cref="CreateUniqueTopologyPrefix"/>, so they can run against the same
/// containers without colliding.
/// </summary>
public sealed class IntegrationEnvironmentFixture : IAsyncLifetime
{
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbitMq;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .Build();

        _rabbitMq = new RabbitMqBuilder()
            .WithImage("rabbitmq:4.0-management-alpine")
            .Build();

        try
        {
            await WaitForReadyAsync(_postgres.StartAsync);
            await WaitForReadyAsync(_rabbitMq.StartAsync);
        }
        catch
        {
            try
            {
                await DisposeAsync();
            }
            catch
            {
                // Preserve the container startup failure.
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a uniquely named database on the shared PostgreSQL container and
    /// applies the production EF Core migrations to it. Never uses EnsureCreated:
    /// production schema ownership stays with the migrations.
    /// </summary>
    public async Task<(MessageBridgeDbContext DbContext, string DatabaseName)> CreateMigratedDatabaseAsync()
    {
        var (connectionString, databaseName) = await CreateDatabaseAsync();

        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        MessageBridgeDbContext? dbContext = null;

        try
        {
            dbContext = new MessageBridgeDbContext(options);
            await dbContext.Database.MigrateAsync();
        }
        catch
        {
            if (dbContext is not null)
            {
                await TryDisposeAsync(dbContext);
            }

            await TryDropDatabaseAsync(databaseName);
            throw;
        }

        return (dbContext, databaseName);
    }

    /// <summary>
    /// Creates a uniquely named empty database for tests that own their EF Core model.
    /// The caller must remove it with <see cref="DropDatabaseAsync"/>.
    /// </summary>
    public async Task<(string ConnectionString, string DatabaseName)> CreateDatabaseAsync()
    {
        var databaseName = $"messagebridge_it_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(_postgres!.GetConnectionString());
        await adminConnection.OpenAsync();
        await using var createCommand = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"",
            adminConnection);
        await createCommand.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = databaseName
        };
        return (builder.ConnectionString, databaseName);
    }

    public async Task DropDatabaseAsync(string databaseName)
    {
        var databaseConnectionString = new NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = databaseName
        }.ConnectionString;
        using var databaseConnection = new NpgsqlConnection(databaseConnectionString);
        NpgsqlConnection.ClearPool(databaseConnection);

        await using var adminConnection = new NpgsqlConnection(_postgres!.GetConnectionString());
        await adminConnection.OpenAsync();

        await using (var terminateCommand = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid();",
            adminConnection))
        {
            terminateCommand.Parameters.AddWithValue("name", databaseName);
            await terminateCommand.ExecuteNonQueryAsync();
        }

        await using var dropCommand = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", adminConnection);
        await dropCommand.ExecuteNonQueryAsync();
    }

    /// <summary>Raw RabbitMQ connection string, for tests that need to connect to the broker.</summary>
    public string GetRabbitMqConnectionString() => _rabbitMq!.GetConnectionString();

    /// <summary>RabbitMQ connection string with credentials redacted, safe for logging/reporting.</summary>
    public string GetRedactedRabbitMqConnectionString()
    {
        var builder = new UriBuilder(_rabbitMq!.GetConnectionString());
        if (!string.IsNullOrEmpty(builder.Password))
        {
            builder.Password = "*****";
        }

        if (!string.IsNullOrEmpty(builder.UserName))
        {
            builder.UserName = "*****";
        }

        return builder.Uri.ToString();
    }

    /// <summary>Unique prefix for RabbitMQ topology (exchanges/queues) so parallel tests don't collide.</summary>
    public static string CreateUniqueTopologyPrefix() => $"it-{Guid.NewGuid():N}";

    /// <summary>Polls until the environment is ready, bounded by the 120-second readiness ceiling.</summary>
    public static Task<T> PollUntilReadyAsync<T>(Func<Task<T?>> probe, string? timeoutMessage = null)
        where T : class
        => PollUntilAsync(probe, ReadinessTimeout, timeoutMessage ?? $"Condition was not ready within {ReadinessTimeout}.");

    /// <summary>Polls until an assertion condition holds, bounded by the 30-second assertion ceiling.</summary>
    public static Task<T> PollUntilAssertedAsync<T>(Func<Task<T?>> probe, string? timeoutMessage = null)
        where T : class
        => PollUntilAsync(probe, AssertionTimeout, timeoutMessage ?? $"Condition was not met within {AssertionTimeout}.");

    /// <summary>Polls for an observation window and fails as soon as the condition changes.</summary>
    public static async Task AssertRemainsAsync(
        Func<Task<bool>> probe,
        TimeSpan observationWindow,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(observationWindow);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await probe())
            {
                throw new InvalidOperationException(failureMessage);
            }

            await Task.Delay(PollInterval);
        }
    }

    private static async Task<T> PollUntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string timeoutMessage)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static async Task WaitForReadyAsync(Func<CancellationToken, Task> startAsync)
    {
        using var cancellation = new CancellationTokenSource(ReadinessTimeout);

        try
        {
            await startAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Container did not become ready within {ReadinessTimeout}.");
        }
    }

    private static async Task TryDisposeAsync(MessageBridgeDbContext dbContext)
    {
        try
        {
            await dbContext.DisposeAsync();
        }
        catch
        {
            // Preserve the migration failure.
        }
    }

    private async Task TryDropDatabaseAsync(string databaseName)
    {
        try
        {
            await DropDatabaseAsync(databaseName);
        }
        catch
        {
            // Preserve the migration failure.
        }
    }
}

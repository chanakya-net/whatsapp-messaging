using System.Security.Cryptography;
using DotNet.Testcontainers.Containers;
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
    private const string DelayedExchangePluginUrl =
        "https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/"
        + "v4.0.7/rabbitmq_delayed_message_exchange-v4.0.7.ez";
    private const string DelayedExchangePluginPath =
        "/opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-v4.0.7.ez";
    private const string DelayedExchangePluginSha256 =
        "9f746962d8f4e9ec2ce52fc86856859c30ed11abc67dd93cd80ebb3ef925d3fd";
    private const string PostgreSqlUsername = "messagebridge_postgres_user";
    private const string PostgreSqlPassword = "messagebridge-postgres-pass-36";
    private const string RabbitMqUsername = "messagebridge_rabbit_user";
    private const string RabbitMqPassword = "messagebridge-rabbit-pass-36";

    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbitMq;

    public async Task InitializeAsync()
    {
        try
        {
            var delayedExchangePlugin = await DownloadDelayedExchangePluginAsync();
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithUsername(PostgreSqlUsername)
                .WithPassword(PostgreSqlPassword)
                .Build();

            _rabbitMq = new RabbitMqBuilder()
                .WithImage("rabbitmq:4.0-management-alpine")
                .WithUsername(RabbitMqUsername)
                .WithPassword(RabbitMqPassword)
                .WithResourceMapping(delayedExchangePlugin, DelayedExchangePluginPath)
                .Build();

            await WaitForReadyAsync(_postgres.StartAsync);
            await WaitForReadyAsync(_rabbitMq.StartAsync);
            await EnableDelayedExchangePluginAsync(_rabbitMq);
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
        var failures = new List<string>();
        var rabbitMq = _rabbitMq;
        var postgres = _postgres;
        _rabbitMq = null;
        _postgres = null;

        if (rabbitMq is not null)
        {
            await CaptureAndDisposeAsync(
                rabbitMq,
                "rabbitmq",
                rabbitMq.GetConnectionString,
                failures);
        }

        if (postgres is not null)
        {
            await CaptureAndDisposeAsync(
                postgres,
                "postgres",
                postgres.GetConnectionString,
                failures);
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                ContainerLogSanitizer.Sanitize(string.Join(Environment.NewLine, failures)));
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

    public static Dictionary<string, string?> CreateDatabaseSettings(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new Dictionary<string, string?>
        {
            ["Database:Host"] = builder.Host,
            ["Database:Port"] = builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Database"] = builder.Database,
            ["Database:Username"] = builder.Username,
            ["Database:Password"] = builder.Password,
            ["Database:UseEntraAuth"] = "false",
            ["Database:MaxPoolSize"] = "12"
        };
    }

    /// <summary>Returns the depth of a queue with the exact topology name.</summary>
    public async Task<int> GetQueueDepthAsync(string queueName)
    {
        var result = await _rabbitMq!.ExecAsync(["rabbitmqctl", "list_queues", "name", "messages"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not read RabbitMQ queue depth: {result.Stderr}");
        }

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length == 2 && columns[0] == queueName)
            {
                return int.TryParse(columns[1], out var depth) ? depth : 0;
            }
        }

        return 0;
    }

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

    /// <summary>Polls until a boolean assertion condition holds.</summary>
    public static async Task PollUntilAssertedAsync(
        Func<Task<bool>> probe,
        string? timeoutMessage = null)
    {
        await PollUntilAsync(
            async () => await probe() ? BooleanProbeResult.Instance : null,
            AssertionTimeout,
            timeoutMessage ?? $"Condition was not met within {AssertionTimeout}.");
    }

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

    private static async Task<byte[]> DownloadDelayedExchangePluginAsync()
    {
        using var cancellation = new CancellationTokenSource(ReadinessTimeout);
        using var httpClient = new HttpClient();
        var plugin = await httpClient.GetByteArrayAsync(
            DelayedExchangePluginUrl,
            cancellation.Token);
        var expectedHash = Convert.FromHexString(DelayedExchangePluginSha256);
        var actualHash = SHA256.HashData(plugin);

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException("RabbitMQ delayed exchange plugin checksum mismatch.");
        }

        return plugin;
    }

    private static async Task EnableDelayedExchangePluginAsync(RabbitMqContainer rabbitMq)
    {
        var result = await rabbitMq.ExecAsync(
                ["rabbitmq-plugins", "enable", "rabbitmq_delayed_message_exchange"])
            .WaitAsync(AssertionTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "RabbitMQ delayed exchange plugin could not be enabled.");
        }
    }

    private static async Task CaptureAndDisposeAsync(
        IContainer container,
        string containerName,
        Func<string> getConnectionString,
        ICollection<string> failures)
    {
        var captureFailure = await ContainerLogCapture.CaptureAsync(
            container,
            containerName,
            getConnectionString);
        if (captureFailure is not null)
        {
            failures.Add(captureFailure);
        }

        try
        {
            await container.DisposeAsync();
        }
        catch (Exception exception)
        {
            failures.Add($"{containerName} cleanup failed ({exception.GetType().Name}).");
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

    private sealed class BooleanProbeResult
    {
        internal static readonly BooleanProbeResult Instance = new();
    }
}

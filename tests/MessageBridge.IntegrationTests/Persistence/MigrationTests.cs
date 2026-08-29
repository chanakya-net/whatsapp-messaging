using System.Diagnostics;
using System.Globalization;
using System.IO;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace MessageBridge.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class MigrationTests(IntegrationEnvironmentFixture fixture)
{
    private const string DockerfilePath = "src/MessageBridge.Worker/Dockerfile.migrate";
    private const string TestImageName = "ghcr.io/chanakya-net/whatsapp-messaging/migrate-contract-test";
    private const string TableName = "message_processing_history";
    private const string CreatedAtIndexName = "IX_message_processing_history_created_at";
    private const string MessageIdTypeIndexName = "IX_message_processing_history_message_id_message_type";
    private const string StatusIndexName = "IX_message_processing_history_status";

    private readonly IntegrationEnvironmentFixture _fixture = fixture;
    private static readonly string RepoRoot = LocateRepoRoot();
    private static readonly string[] MigrationPlatforms = ["linux/amd64", "linux/arm64"];
    private static readonly Dictionary<string, string> BuiltImages = new();
    private static readonly SemaphoreSlim MigrationImageBuildGate = new(1, 1);

    [Fact]
    public async Task Migrations_apply_to_empty_database()
    {
        var (dbContext, databaseName) = await _fixture.CreateMigratedDatabaseAsync();

        try
        {
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            var columns = await GetColumnsAsync(connection);
            columns.ShouldContainKeyAndValue("id", "uuid");
            columns.ShouldContainKeyAndValue("message_id", "text");
            columns.ShouldContainKeyAndValue("message_type", "text");
            columns.ShouldContainKeyAndValue("status", "text");
            columns.ShouldContainKeyAndValue("payload_hash", "text");
            columns.ShouldContainKeyAndValue("provider", "text");
            columns.ShouldContainKeyAndValue("provider_metadata", "jsonb");
            columns.ShouldContainKeyAndValue("failure_reason", "text");
            columns.ShouldContainKeyAndValue("attempt_count", "integer");
            columns.ShouldContainKeyAndValue("created_at", "timestamp with time zone");
            columns.ShouldContainKeyAndValue("updated_at", "timestamp with time zone");
            columns.ShouldContainKeyAndValue("processed_at", "timestamp with time zone");

            var indexNames = await GetIndexNamesAsync(connection);
            indexNames.ShouldContain(StatusIndexName);
            indexNames.ShouldContain(CreatedAtIndexName);

            var hasUniqueMessageIdTypeConstraint = await HasUniqueIndexAsync(connection, MessageIdTypeIndexName);
            hasUniqueMessageIdTypeConstraint.ShouldBeTrue();
        }
        finally
        {
            await dbContext.DisposeAsync();
            await _fixture.DropDatabaseAsync(databaseName);
        }
    }

    public static IEnumerable<object[]> SupportedMigrationPlatforms()
    {
        return MigrationPlatforms.Select(platform => new object[] { platform });
    }

    [Theory]
    [MemberData(nameof(SupportedMigrationPlatforms))]
    public async Task Migration_image_applies_migrations_for_supported_architectures(string platform)
    {
        var (connectionString, databaseName) = await _fixture.CreateDatabaseAsync();
        var imageTag = await BuildMigrationImageAsync(platform);

        try
        {
            var runResult = await RunMigrationContainerAsync(platform, imageTag, connectionString);
            runResult.ExitCode.ShouldBe(0, runResult.StandardError);

            await VerifyMigrationSchemaAsync(connectionString);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [MemberData(nameof(SupportedMigrationPlatforms))]
    public async Task Migration_image_fails_with_invalid_connection_credentials(string platform)
    {
        var (connectionString, databaseName) = await _fixture.CreateDatabaseAsync();
        var imageTag = await BuildMigrationImageAsync(platform);

        var invalidCredentials = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = "wrong-password",
        }.ConnectionString;

        try
        {
            var runResult = await RunMigrationContainerAsync(platform, imageTag, invalidCredentials);
            runResult.ExitCode.ShouldNotBe(0, runResult.StandardError);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task VerifyMigrationSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var columns = await GetColumnsAsync(connection);
        columns.ShouldContainKeyAndValue("id", "uuid");
        columns.ShouldContainKeyAndValue("message_id", "text");
        columns.ShouldContainKeyAndValue("message_type", "text");
        columns.ShouldContainKeyAndValue("status", "text");
        columns.ShouldContainKeyAndValue("payload_hash", "text");
        columns.ShouldContainKeyAndValue("provider", "text");
        columns.ShouldContainKeyAndValue("provider_metadata", "jsonb");
        columns.ShouldContainKeyAndValue("failure_reason", "text");
        columns.ShouldContainKeyAndValue("attempt_count", "integer");
        columns.ShouldContainKeyAndValue("created_at", "timestamp with time zone");
        columns.ShouldContainKeyAndValue("updated_at", "timestamp with time zone");
        columns.ShouldContainKeyAndValue("processed_at", "timestamp with time zone");

        var indexNames = await GetIndexNamesAsync(connection);
        indexNames.ShouldContain(StatusIndexName);
        indexNames.ShouldContain(CreatedAtIndexName);

        var hasUniqueMessageIdTypeConstraint = await HasUniqueIndexAsync(
            connection,
            MessageIdTypeIndexName);
        hasUniqueMessageIdTypeConstraint.ShouldBeTrue();
    }

    private static async Task<string> BuildMigrationImageAsync(string platform)
    {
        if (BuiltImages.TryGetValue(platform, out var existing))
        {
            return existing;
        }

        await MigrationImageBuildGate.WaitAsync();
        try
        {
            if (BuiltImages.TryGetValue(platform, out existing))
            {
                return existing;
            }

            var imageTag = $"{TestImageName}:{platform.Replace('/', '-')}";
            var result = await RunCommandAsync(
                "docker",
                [
                    "buildx",
                    "build",
                    "--platform",
                    platform,
                    "--load",
                    "-f",
                    Path.Combine(RepoRoot, DockerfilePath),
                    "-t",
                    imageTag,
                    RepoRoot,
                ]);

            result.ExitCode.ShouldBe(0, result.StandardError);
            BuiltImages[platform] = imageTag;
            return imageTag;
        }
        finally
        {
            MigrationImageBuildGate.Release();
        }
    }

    private static async Task<MigrationRunResult> RunMigrationContainerAsync(
        string platform,
        string imageTag,
        string connectionString)
    {
        var parsedConnection = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseHost = GetMigrationContainerHost(parsedConnection.Host ?? "localhost");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Database__Host"] = databaseHost,
            ["Database__Port"] = parsedConnection.Port.ToString(CultureInfo.InvariantCulture),
            ["Database__Database"] = parsedConnection.Database,
            ["Database__Username"] = parsedConnection.Username,
            ["Database__Password"] = parsedConnection.Password,
            ["Database__UseEntraAuth"] = "false",
            ["Database__MaxPoolSize"] = "4",
        };

        var args = new List<string>
        {
            "run",
            "--rm",
            "--platform",
            platform,
            "--name",
            $"migration-it-{Guid.NewGuid():N}"
        };

        if (databaseHost == "host.docker.internal")
        {
            args.AddRange(["--add-host", "host.docker.internal:host-gateway"]);
        }

        foreach (var (name, value) in values)
        {
            args.AddRange(["--env", $"{name}={value}"]);
        }

        args.Add(imageTag);

        return await RunCommandAsync("docker", args);
    }

    private static string GetMigrationContainerHost(string host)
        => IsLoopbackHost(host) ? "host.docker.internal" : host;

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.Ordinal)
        || host.Equals("[::1]", StringComparison.Ordinal);

    private static async Task<MigrationRunResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        return new MigrationRunResult(process.ExitCode, output, error);
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MessageBridge.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test binary path.");
    }

    private static async Task<Dictionary<string, string>> GetColumnsAsync(NpgsqlConnection connection)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = @table;",
            connection);
        command.Parameters.AddWithValue("table", TableName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }

    private static async Task<List<string>> GetIndexNamesAsync(NpgsqlConnection connection)
    {
        var names = new List<string>();
        await using var command = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE tablename = @table;",
            connection);
        command.Parameters.AddWithValue("table", TableName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<bool> HasUniqueIndexAsync(NpgsqlConnection connection, string indexName)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT i.indisunique
            FROM pg_index i
            JOIN pg_class table_class ON table_class.oid = i.indrelid
            JOIN pg_class index_class ON index_class.oid = i.indexrelid
            WHERE table_class.relname = @table AND index_class.relname = @indexName;
            """,
            connection);
        command.Parameters.AddWithValue("table", TableName);
        command.Parameters.AddWithValue("indexName", indexName);

        return await command.ExecuteScalarAsync() is true;
    }

    private sealed record MigrationRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

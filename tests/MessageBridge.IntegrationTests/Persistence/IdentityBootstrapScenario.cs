using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MessageBridge.IntegrationTests.Persistence;

internal sealed class IdentityBootstrapScenario : IAsyncDisposable
{
    private const string RolePassword = "identity-bootstrap-test-password";
    private static readonly string BootstrapSql = File.ReadAllText(
        Path.Combine(LocateRepoRoot(), "scripts/db/bootstrap-identities.sql"));

    private readonly IntegrationEnvironmentFixture _fixture;
    private readonly MessageBridgeDbContext _dbContext;
    private readonly List<IdentityDatabaseTarget> _targets;

    private IdentityBootstrapScenario(
        IntegrationEnvironmentFixture fixture,
        MessageBridgeDbContext dbContext,
        IdentityDatabaseTarget development)
    {
        _fixture = fixture;
        _dbContext = dbContext;
        Development = development;
        _targets = [development];
    }

    public IdentityDatabaseTarget Development { get; }

    public string RuntimeRole => Development.RuntimeRole;

    public string MigratorRole => Development.MigratorRole;

    public static async Task<IdentityBootstrapScenario> CreateMigratedAsync(
        IntegrationEnvironmentFixture fixture)
    {
        var (dbContext, databaseName) = await fixture.CreateMigratedDatabaseAsync();
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Migrated database has no connection string.");
        return new IdentityBootstrapScenario(
            fixture,
            dbContext,
            CreateTarget(connectionString, databaseName));
    }

    public async Task<IdentityDatabaseTarget> CreateEmptyTargetAsync()
    {
        var (connectionString, databaseName) = await _fixture.CreateDatabaseAsync();
        var target = CreateTarget(connectionString, databaseName);
        _targets.Add(target);
        return target;
    }

    public Task ApplyAsync() => RunBootstrapAsync(Development, "apply");

    public Task ApplyAsync(IdentityDatabaseTarget target) => RunBootstrapAsync(target, "apply");

    public Task VerifyAsync(IdentityDatabaseTarget target) => RunBootstrapAsync(target, "verify");

    public Task<NpgsqlConnection> OpenRuntimeConnectionAsync()
        => OpenRoleConnectionAsync(Development.RuntimeRole, Development);

    public Task<NpgsqlConnection> OpenMigratorConnectionAsync()
        => OpenRoleConnectionAsync(Development.MigratorRole, Development);

    public Task<NpgsqlConnection> OpenRuntimeConnectionAsync(IdentityDatabaseTarget target)
        => OpenRoleConnectionAsync(target.RuntimeRole, target);

    public Task<NpgsqlConnection> OpenDevelopmentRuntimeConnectionToAsync(
        IdentityDatabaseTarget target)
        => OpenRoleConnectionAsync(Development.RuntimeRole, target);

    public Task<NpgsqlConnection> OpenDevelopmentMigratorConnectionToAsync(
        IdentityDatabaseTarget target)
        => OpenRoleConnectionAsync(Development.MigratorRole, target);

    public Task<NpgsqlConnection> OpenProductionRuntimeConnectionToAsync(
        IdentityDatabaseTarget production,
        IdentityDatabaseTarget target)
        => OpenRoleConnectionAsync(production.RuntimeRole, target);

    public Task<NpgsqlConnection> OpenProductionMigratorConnectionToAsync(
        IdentityDatabaseTarget production,
        IdentityDatabaseTarget target)
        => OpenRoleConnectionAsync(production.MigratorRole, target);

    public async Task<string> GetRoleIdentifierAsync(string role)
    {
        await using var connection = new NpgsqlConnection(Development.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT oid::text FROM pg_catalog.pg_roles WHERE rolname = @role;",
            connection);
        command.Parameters.AddWithValue("role", role);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Role {role} does not exist."));
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await CleanupOwnershipAsync();
        await DropRolesAsync();
        foreach (var target in _targets.AsEnumerable().Reverse())
        {
            await _fixture.DropDatabaseAsync(target.DatabaseName);
        }
    }

    private async Task RunBootstrapAsync(IdentityDatabaseTarget target, string mode)
    {
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await SetBootstrapSettingsAsync(connection, target, mode);
        await ExecuteAsync(connection, BootstrapSql);
        if (mode == "apply")
        {
            await SetRolePasswordAsync(connection, target.RuntimeRole);
            await SetRolePasswordAsync(connection, target.MigratorRole);
        }
    }

    private static async Task<NpgsqlConnection> OpenRoleConnectionAsync(
        string role,
        IdentityDatabaseTarget database)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = RolePassword,
            Pooling = false,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task CleanupOwnershipAsync()
    {
        foreach (var target in _targets)
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            foreach (var role in Roles)
            {
                if (!await RoleExistsAsync(connection, role))
                {
                    continue;
                }

                var identifier = QuoteIdentifier(role);
                await ExecuteAsync(connection, $"REASSIGN OWNED BY {identifier} TO CURRENT_USER;");
                await ExecuteAsync(connection, $"DROP OWNED BY {identifier};");
            }
        }
    }

    private async Task DropRolesAsync()
    {
        await using var connection = new NpgsqlConnection(Development.ConnectionString);
        await connection.OpenAsync();
        foreach (var role in Roles)
        {
            if (!await RoleExistsAsync(connection, role))
            {
                continue;
            }

            var identifier = QuoteIdentifier(role);
            await ExecuteAsync(connection, $"REVOKE {identifier} FROM CURRENT_USER;");
            await ExecuteAsync(connection, $"DROP ROLE {identifier};");
        }
    }

    private IEnumerable<string> Roles => _targets
        .SelectMany(target => new[] { target.RuntimeRole, target.MigratorRole })
        .Distinct(StringComparer.Ordinal);

    private static async Task SetBootstrapSettingsAsync(
        NpgsqlConnection connection,
        IdentityDatabaseTarget target,
        string mode)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('messagebridge.runtime_role', @runtime, false),
                   set_config('messagebridge.migrator_role', @migrator, false),
                   set_config('messagebridge.bootstrap_mode', @mode, false);
            """,
            connection);
        command.Parameters.AddWithValue("runtime", target.RuntimeRole);
        command.Parameters.AddWithValue("migrator", target.MigratorRole);
        command.Parameters.AddWithValue("mode", mode);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetRolePasswordAsync(NpgsqlConnection connection, string role)
        => await ExecuteAsync(
            connection,
            $"ALTER ROLE {QuoteIdentifier(role)} PASSWORD {QuoteLiteral(RolePassword)};");

    private static async Task<bool> RoleExistsAsync(NpgsqlConnection connection, string role)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = @role);",
            connection);
        command.Parameters.AddWithValue("role", role);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static IdentityDatabaseTarget CreateTarget(string connectionString, string databaseName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new IdentityDatabaseTarget(
            connectionString,
            databaseName,
            $"mb_runtime_{suffix}",
            $"mb_migrator_{suffix}");
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

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
}

internal sealed record IdentityDatabaseTarget(
    string ConnectionString,
    string DatabaseName,
    string RuntimeRole,
    string MigratorRole);

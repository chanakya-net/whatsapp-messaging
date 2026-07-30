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
    private const string TableName = "message_processing_history";
    private const string CreatedAtIndexName = "IX_message_processing_history_created_at";
    private const string MessageIdTypeIndexName = "IX_message_processing_history_message_id_message_type";
    private const string StatusIndexName = "IX_message_processing_history_status";

    private readonly IntegrationEnvironmentFixture _fixture = fixture;

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
}

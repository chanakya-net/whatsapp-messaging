using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;

namespace MessageBridge.IntegrationTests.Persistence;

internal sealed class MigratedDatabaseScenario : IAsyncDisposable
{
    private readonly IntegrationEnvironmentFixture _fixture;
    private readonly string _databaseName;

    private MigratedDatabaseScenario(
        IntegrationEnvironmentFixture fixture,
        MessageBridgeDbContext dbContext,
        string databaseName)
    {
        _fixture = fixture;
        DbContext = dbContext;
        _databaseName = databaseName;
    }

    public MessageBridgeDbContext DbContext { get; }

    public static async Task<MigratedDatabaseScenario> CreateAsync(
        IntegrationEnvironmentFixture fixture)
    {
        var (dbContext, databaseName) = await fixture.CreateMigratedDatabaseAsync();
        return new MigratedDatabaseScenario(fixture, dbContext, databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _fixture.DropDatabaseAsync(_databaseName);
    }
}

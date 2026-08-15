using Azure.Core;
using System.Data.Common;
using System.Reflection;
using MessageBridge.Infrastructure;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Persistence;

[Trait("Category", "Unit")]
public sealed class NpgsqlDataSourceRegistrationTests
{
    [Fact]
    public async Task Entra_factory_uses_selected_identity_scope_and_refresh_policy()
    {
        var options = ValidEntraOptions();
        var credential = new RecordingTokenCredential();

        await using var dataSource = NpgsqlDataSourceFactory.Create(options, credential);
        var request = await credential.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        request.Scopes.ShouldBe(new[] { NpgsqlDataSourceFactory.AzurePostgreSqlScope });
        NpgsqlDataSourceFactory.SuccessRefreshInterval.ShouldBe(TimeSpan.FromMinutes(50));
        NpgsqlDataSourceFactory.FailureRefreshInterval.ShouldBe(TimeSpan.FromSeconds(5));
        NpgsqlDataSourceFactory.CreateDefaultAzureCredentialOptions(options)
            .ManagedIdentityClientId.ShouldBe("identity-client-id");
        dataSource.ConnectionString.ShouldContain("SSL Mode=Require");
        dataSource.ConnectionString.ShouldNotContain("Password", Case.Insensitive);
    }

    [Fact]
    public async Task DI_reuses_one_data_source_for_scoped_and_factory_contexts()
    {
        var services = new ServiceCollection();
        services.AddMessageBridgeProcessingStore(LocalConfiguration());

        services.Count(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource))
            .ShouldBe(1);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        provider.GetRequiredService<NpgsqlDataSource>().ShouldBeSameAs(dataSource);

        await using var scope = provider.CreateAsyncScope();
        var scopedContext = scope.ServiceProvider.GetRequiredService<MessageBridgeDbContext>();
        var factory = provider.GetRequiredService<IDbContextFactory<MessageBridgeDbContext>>();
        await using var factoryContext = await factory.CreateDbContextAsync();

        GetDataSource(scopedContext).ShouldBeSameAs(dataSource);
        GetDataSource(factoryContext).ShouldBeSameAs(dataSource);
    }

    private static DatabaseOptions ValidEntraOptions() => new()
    {
        Host = "database.postgres.database.azure.com",
        Database = "messagebridge",
        Username = "worker-identity",
        UseEntraAuth = true,
        ManagedIdentityClientId = "identity-client-id"
    };

    private static IConfiguration LocalConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Host"] = "unit-test",
            ["Database:Database"] = "messagebridge",
            ["Database:Username"] = "postgres",
            ["Database:Password"] = "p;a=s\"word",
            ["Database:MaxPoolSize"] = "17"
        })
        .Build();

    private static DbDataSource? GetDataSource(DbContext context)
    {
        var connection = context.GetService<IRelationalConnection>();
        return connection.GetType()
            .GetProperty(
                "DbDataSource",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(connection) as DbDataSource;
    }

    private sealed class RecordingTokenCredential : TokenCredential
    {
        public TaskCompletionSource<TokenRequestContext> Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => Record(requestContext);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Record(requestContext));

        private AccessToken Record(TokenRequestContext requestContext)
        {
            Requested.TrySetResult(requestContext);
            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }
    }
}

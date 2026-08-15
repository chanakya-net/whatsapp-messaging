using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MessageBridge.Infrastructure.Persistence;

public static class NpgsqlDataSourceFactory
{
    internal const string AzurePostgreSqlScope =
        "https://ossrdbms-aad.database.windows.net/.default";

    internal static readonly TimeSpan SuccessRefreshInterval = TimeSpan.FromMinutes(50);
    internal static readonly TimeSpan FailureRefreshInterval = TimeSpan.FromSeconds(5);

    public static NpgsqlDataSource Create(DatabaseOptions options)
    {
        var credential = new DefaultAzureCredential(CreateDefaultAzureCredentialOptions(options));
        return Create(options, credential);
    }

    internal static NpgsqlDataSource Create(DatabaseOptions options, TokenCredential credential)
    {
        Validate(options);
        var builder = new NpgsqlDataSourceBuilder(
            CreateConnectionStringBuilder(options).ConnectionString);

        if (options.UseEntraAuth)
        {
            builder.UsePeriodicPasswordProvider(
                async (_, cancellationToken) =>
                {
                    var context = new TokenRequestContext([AzurePostgreSqlScope]);
                    var token = await credential.GetTokenAsync(context, cancellationToken);
                    return token.Token;
                },
                SuccessRefreshInterval,
                FailureRefreshInterval);
        }

        return builder.Build();
    }

    public static NpgsqlConnectionStringBuilder CreateConnectionStringBuilder(DatabaseOptions options)
        => new()
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Password = options.UseEntraAuth ? null : options.Password,
            SslMode = options.UseEntraAuth ? SslMode.Require : SslMode.Prefer,
            MaxPoolSize = options.MaxPoolSize
        };

    internal static DefaultAzureCredentialOptions CreateDefaultAzureCredentialOptions(
        DatabaseOptions options)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
            credentialOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;

        return credentialOptions;
    }

    private static void Validate(DatabaseOptions options)
    {
        var validation = new DatabaseOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                DatabaseOptions.SectionName,
                typeof(DatabaseOptions),
                validation.Failures);
        }
    }
}

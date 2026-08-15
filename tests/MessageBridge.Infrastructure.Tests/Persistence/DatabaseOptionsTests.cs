using MessageBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Persistence;

[Trait("Category", "Unit")]
public sealed class DatabaseOptionsTests
{
    [Fact]
    public void Local_password_mode_builds_safe_configured_connection_settings()
    {
        var options = new DatabaseOptions
        {
            Host = "localhost",
            Port = 5544,
            Database = "messagebridge",
            Username = "postgres",
            Password = "p;a=s\"word",
            MaxPoolSize = 27
        };

        var validation = new DatabaseOptionsValidator().Validate(null, options);
        var builder = NpgsqlDataSourceFactory.CreateConnectionStringBuilder(options);

        validation.Succeeded.ShouldBeTrue();
        builder.Host.ShouldBe("localhost");
        builder.Port.ShouldBe(5544);
        builder.Database.ShouldBe("messagebridge");
        builder.Username.ShouldBe("postgres");
        builder.Password.ShouldBe("p;a=s\"word");
        builder.MaxPoolSize.ShouldBe(27);
        builder.SslMode.ShouldBe(SslMode.Prefer);
    }

    [Fact]
    public void Local_password_mode_rejects_blank_password()
    {
        var options = ValidOptions();
        options.Password = "   ";

        var validation = new DatabaseOptionsValidator().Validate(null, options);

        validation.Failed.ShouldBeTrue();
        validation.Failures.ShouldContain("Password is required when UseEntraAuth is disabled.");
    }

    [Fact]
    public void Entra_mode_requires_tls_and_forbids_stored_passwords()
    {
        var options = ValidOptions();
        options.UseEntraAuth = true;
        options.Password = null;

        var validResult = new DatabaseOptionsValidator().Validate(null, options);
        var builder = NpgsqlDataSourceFactory.CreateConnectionStringBuilder(options);

        validResult.Succeeded.ShouldBeTrue();
        builder.SslMode.ShouldBe(SslMode.Require);
        builder.Password.ShouldBeNull();
        builder.ConnectionString.ShouldNotContain("Password", Case.Insensitive);

        options.Password = "must-not-be-stored";
        var invalidResult = new DatabaseOptionsValidator().Validate(null, options);

        invalidResult.Failed.ShouldBeTrue();
        invalidResult.Failures.ShouldContain(
            "Password must not be stored when UseEntraAuth is enabled.");
    }

    [Theory]
    [InlineData("Host", "", 5432, 12)]
    [InlineData("Database", "", 5432, 12)]
    [InlineData("Username", "", 5432, 12)]
    [InlineData("Port", null, 0, 12)]
    [InlineData("Port", null, 65536, 12)]
    [InlineData("MaxPoolSize", null, 5432, 0)]
    public void Required_fields_and_numeric_bounds_are_validated(
        string property,
        string? value,
        int port,
        int maxPoolSize)
    {
        var options = ValidOptions();
        options.Port = port;
        options.MaxPoolSize = maxPoolSize;

        switch (property)
        {
            case "Host": options.Host = value!; break;
            case "Database": options.Database = value!; break;
            case "Username": options.Username = value!; break;
        }

        var validation = new DatabaseOptionsValidator().Validate(null, options);

        validation.Failed.ShouldBeTrue();
        validation.Failures.ShouldContain(failure => failure.StartsWith(property));
    }

    [Fact]
    public void Default_port_and_pool_size_are_applied()
    {
        var options = ValidOptions();
        var builder = NpgsqlDataSourceFactory.CreateConnectionStringBuilder(options);

        options.Port.ShouldBe(5432);
        options.MaxPoolSize.ShouldBe(12);
        builder.Port.ShouldBe(5432);
        builder.MaxPoolSize.ShouldBe(12);
    }

    private static DatabaseOptions ValidOptions() => new()
    {
        Host = "localhost",
        Database = "messagebridge",
        Username = "postgres",
        Password = "postgres"
    };
}

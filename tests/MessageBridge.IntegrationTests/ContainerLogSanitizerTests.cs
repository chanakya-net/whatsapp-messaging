using FluentAssertions;
using MessageBridge.IntegrationTests.Fixtures;

namespace MessageBridge.IntegrationTests;

[Trait("Category", "Unit")]
public sealed class ContainerLogSanitizerTests
{
    [Fact]
    public void Sanitize_removes_all_sensitive_diagnostic_values()
    {
        const string postgresConnection =
            "Host=localhost;Database=bridge;Username=container-admin;Password=pg-secret-36";
        const string rabbitConnection =
            "amqp://rabbit-user-36:rabbit-pass-36@localhost:5672/";
        const string diagnostics = """
            startup: database and broker probes completed
            connection: Host=localhost;Username=container-admin;Password=pg-secret-36
            broker: amqp://rabbit-user-36:rabbit-pass-36@localhost:5672/
            raw credential echo: container-admin pg-secret-36 rabbit-user-36 rabbit-pass-36
            PLAIN login refused: user 'invalid_rabbit_user_32' - invalid credentials
            password=field-secret token=token-secret Authorization: Bearer auth-secret
            recipients: person@example.com and +1 (415) 555-2671
            payload={"body":"private structured payload","token":"inside-secret"}
            payload=private text payload
            shutdown: log capture completed
            """;

        var sanitized = ContainerLogSanitizer.Sanitize(
            diagnostics,
            postgresConnection,
            rabbitConnection);

        var sensitiveValues = new[]
        {
            "container-admin",
            "pg-secret-36",
            "rabbit-user-36",
            "rabbit-pass-36",
            "invalid_rabbit_user_32",
            "field-secret",
            "token-secret",
            "auth-secret",
            "person@example.com",
            "+1 (415) 555-2671",
            "private structured payload",
            "private text payload",
            "inside-secret"
        };
        foreach (var sensitiveValue in sensitiveValues)
        {
            sanitized.Should().NotContain(sensitiveValue);
        }

        sanitized.Should().Contain("startup: database and broker probes completed");
        sanitized.Should().Contain("shutdown: log capture completed");
        sanitized.Should().Contain("REDACTED");
        sanitized.Should().Contain("p***n@***.com");
        sanitized.Should().Contain("2671");
    }
}

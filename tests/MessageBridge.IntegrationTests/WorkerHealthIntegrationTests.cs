using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.IntegrationTests.Persistence;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Infrastructure;
using MessageBridge.Worker.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerHealthIntegrationTests(IntegrationEnvironmentFixture fixture)
{
    [Fact]
    public async Task WorkerHealth_StartupLivenessAndReadiness_AreSanitized()
    {
        await using var harness = await WorkerHealthHarness.StartAsync(fixture);

        await AssertLiveAsync(harness.Client);
        var ready = await harness.WaitForReadinessAsync(
            HttpStatusCode.OK,
            rabbitMqStatus: "Healthy",
            postgresStatus: "Healthy");
        await AssertLiveAsync(harness.Client);

        AssertReadiness(ready, HttpStatusCode.OK, "Healthy", "Healthy", "Healthy");
        AssertSecretsAbsent(
            [ready.Body, ready.Describe(), harness.Logs.CombinedEntries],
            harness.SensitiveConfiguration);
    }

    [Fact]
    public async Task WorkerHealth_LiveButUnready_WhenPostgresMisconfigured()
    {
        const string invalidUsername = "invalid_pg_user_32";
        const string invalidPassword = "invalid_pg_password_32";
        await using var harness = await WorkerHealthHarness.StartAsync(
            fixture,
            configurePostgres: connectionString =>
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Username = invalidUsername,
                    Password = invalidPassword,
                    Timeout = 2,
                    CommandTimeout = 2,
                    Pooling = false
                };
                return builder.ConnectionString;
            });

        await AssertLiveAsync(harness.Client);
        var ready = await harness.WaitForReadinessAsync(
            HttpStatusCode.ServiceUnavailable,
            rabbitMqStatus: "Healthy",
            postgresStatus: "Unhealthy");
        await AssertLiveAsync(harness.Client);

        AssertReadiness(
            ready,
            HttpStatusCode.ServiceUnavailable,
            "Unhealthy",
            "Healthy",
            "Unhealthy");
        AssertSecretsAbsent(
            [ready.Body, ready.Describe(), harness.Logs.CombinedEntries],
            harness.SensitiveConfiguration);
    }

    [Fact]
    public async Task WorkerHealth_LiveButUnready_WhenRabbitMqMisconfigured()
    {
        const string invalidUsername = "invalid_rabbit_user_32";
        const string invalidPassword = "invalid_rabbit_password_32";
        await using var harness = await WorkerHealthHarness.StartAsync(
            fixture,
            configureRabbitMq: connectionString =>
            {
                var builder = new UriBuilder(connectionString)
                {
                    UserName = invalidUsername,
                    Password = invalidPassword
                };
                return builder.Uri.AbsoluteUri;
            });

        await AssertLiveAsync(harness.Client);
        var ready = await harness.WaitForReadinessAsync(
            HttpStatusCode.ServiceUnavailable,
            rabbitMqStatus: "Unhealthy",
            postgresStatus: "Healthy");
        await AssertLiveAsync(harness.Client);

        AssertReadiness(
            ready,
            HttpStatusCode.ServiceUnavailable,
            "Unhealthy",
            "Unhealthy",
            "Healthy");
        AssertSecretsAbsent(
            [ready.Body, ready.Describe(), harness.Logs.CombinedEntries],
            harness.SensitiveConfiguration);
    }

    private static async Task AssertLiveAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        root.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["status"]);
        root.GetProperty("status").GetString().Should().Be("live");
    }

    private static void AssertReadiness(
        ReadinessObservation ready,
        HttpStatusCode statusCode,
        string overallStatus,
        string rabbitMqStatus,
        string postgresStatus)
    {
        ready.StatusCode.Should().Be(statusCode);
        ready.OverallStatus.Should().Be(overallStatus);
        ready.PropertyNames.Should().BeEquivalentTo(["status", "checks"]);
        ready.Checks.Keys.Should().BeEquivalentTo(["rabbitmq", "postgres"]);
        ready.Checks["rabbitmq"].Should().Be(rabbitMqStatus);
        ready.Checks["postgres"].Should().Be(postgresStatus);
        ready.Body.ToLowerInvariant().Should().NotContain("description");
        ready.Body.ToLowerInvariant().Should().NotContain("exception");
    }

    private static void AssertSecretsAbsent(
        IEnumerable<string> observations,
        IEnumerable<string> secrets)
    {
        foreach (var observation in observations)
        {
            foreach (var secret in secrets.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                observation.Should().NotContain(secret);
            }
        }
    }

    private sealed class WorkerHealthHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly MigratedDatabaseScenario _database;

        private WorkerHealthHarness(
            WebApplication app,
            HttpClient client,
            MigratedDatabaseScenario database,
            CapturingLoggerProvider logs,
            IReadOnlyList<string> sensitiveConfiguration)
        {
            _app = app;
            Client = client;
            _database = database;
            Logs = logs;
            SensitiveConfiguration = sensitiveConfiguration;
        }

        public HttpClient Client { get; }

        public CapturingLoggerProvider Logs { get; }

        public IReadOnlyList<string> SensitiveConfiguration { get; }

        public static async Task<WorkerHealthHarness> StartAsync(
            IntegrationEnvironmentFixture fixture,
            Func<string, string>? configurePostgres = null,
            Func<string, string>? configureRabbitMq = null)
        {
            var database = await MigratedDatabaseScenario.CreateAsync(fixture);
            var postgres = database.DbContext.Database.GetConnectionString()!;
            var rabbitMq = fixture.GetRabbitMqConnectionString();
            var configuredPostgres = configurePostgres?.Invoke(postgres) ?? postgres;
            var configuredRabbitMq = configureRabbitMq?.Invoke(rabbitMq) ?? rabbitMq;
            var settings = CreateSettings(configuredPostgres, configuredRabbitMq);
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.Configure<RabbitMqOptions>(
                builder.Configuration.GetSection(RabbitMqOptions.SectionName));
            builder.Services.AddMessageBridgeProcessingStore(builder.Configuration);
            builder.Services.AddMessageBridgeObservability(builder.Configuration);
            var app = builder.Build();
            app.MapMessageBridgeHealthAndMetrics();

            try
            {
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                var client = new HttpClient { BaseAddress = new Uri(address) };
                return new WorkerHealthHarness(
                    app,
                    client,
                    database,
                    logs,
                    GetSensitiveConfiguration(configuredPostgres, configuredRabbitMq));
            }
            catch
            {
                await app.DisposeAsync();
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task<ReadinessObservation> WaitForReadinessAsync(
            HttpStatusCode expectedStatus,
            string rabbitMqStatus,
            string postgresStatus)
        {
            ReadinessObservation? last = null;

            try
            {
                return await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
                    async () =>
                    {
                        last = await ReadinessObservation.ReadAsync(Client);
                        return last.StatusCode == expectedStatus
                            && last.Checks.GetValueOrDefault("rabbitmq") == rabbitMqStatus
                            && last.Checks.GetValueOrDefault("postgres") == postgresStatus
                            ? last
                            : null;
                    },
                    "Worker readiness did not reach the expected sanitized state.");
            }
            catch (TimeoutException exception)
            {
                var rabbitMqDiagnostic = await GetRabbitMqDiagnosticAsync();
                throw new TimeoutException(
                    $"Worker readiness timed out; last={last?.Describe() ?? "not-observed"}; " +
                    $"rabbitmq={rabbitMqDiagnostic}.",
                    exception);
            }
        }

        private async Task<string> GetRabbitMqDiagnosticAsync()
        {
            try
            {
                var probe = _app.Services.GetRequiredService<IRabbitMqReadinessProbe>();
                return await probe.IsReadyAsync(CancellationToken.None)
                    ? "direct-ready"
                    : "direct-not-ready";
            }
            catch (Exception exception)
            {
                return $"probe-error={exception.GetType().Name}";
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _database.DisposeAsync();
            Logs.Dispose();
        }

        private static Dictionary<string, string?> CreateSettings(
            string postgres,
            string rabbitMq)
        {
            var settings = IntegrationEnvironmentFixture.CreateDatabaseSettings(postgres);
            settings.Add("RabbitMq:ConnectionString", rabbitMq);
            settings.Add(
                "MessageBridge:Topology:EnvironmentPrefix",
                IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix());
            settings.Add("MessageBridge:ProcessingHistory:RecoveryEnabled", "false");
            settings.Add("Observability:MetricsEndpointEnabled", "false");
            return settings;
        }

        private static IReadOnlyList<string> GetSensitiveConfiguration(
            string postgres,
            string rabbitMq)
        {
            var postgresBuilder = new NpgsqlConnectionStringBuilder(postgres);
            var rabbitUri = new Uri(rabbitMq);
            var rabbitCredentials = Uri.UnescapeDataString(rabbitUri.UserInfo).Split(':', 2);

            return new[]
            {
                postgres,
                postgresBuilder.Username ?? string.Empty,
                postgresBuilder.Password ?? string.Empty,
                rabbitMq,
                rabbitCredentials.ElementAtOrDefault(0) ?? string.Empty,
                rabbitCredentials.ElementAtOrDefault(1) ?? string.Empty
            };
        }
    }

    private sealed record ReadinessObservation(
        HttpStatusCode StatusCode,
        string OverallStatus,
        IReadOnlyDictionary<string, string> Checks,
        IReadOnlySet<string> PropertyNames,
        string Body)
    {
        public static async Task<ReadinessObservation> ReadAsync(HttpClient client)
        {
            using var response = await client.GetAsync("/health/ready");
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var checks = root.GetProperty("checks")
                .EnumerateObject()
                .ToDictionary(
                    item => item.Name,
                    item => item.Value.GetString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

            return new ReadinessObservation(
                response.StatusCode,
                root.GetProperty("status").GetString() ?? string.Empty,
                checks,
                root.EnumerateObject()
                    .Select(property => property.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                body);
        }

        public string Describe() =>
            $"http={(int)StatusCode}, overall={OverallStatus}, " +
            $"rabbitmq={Checks.GetValueOrDefault("rabbitmq", "missing")}, " +
            $"postgres={Checks.GetValueOrDefault("postgres", "missing")}";
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string CombinedEntries => string.Join(Environment.NewLine, _entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    entries.Enqueue(exception.ToString());
                }
            }
        }
    }
}

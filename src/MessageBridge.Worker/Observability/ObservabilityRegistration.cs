using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using System.Text.Json;

namespace MessageBridge.Worker.Observability;

public static class ObservabilityRegistration
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddMessageBridgeObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);

        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing => AddTracing(tracing, options))
            .WithMetrics(metrics => AddMetrics(metrics, options));

        services.AddSingleton<IRabbitMqReadinessProbe, RabbitMqReadinessProbe>();
        services.AddSingleton<IPostgresReadinessProbe, PostgresReadinessProbe>();

        services.AddHealthChecks()
            .AddCheck<RabbitMqReadinessHealthCheck>("rabbitmq", tags: new[] { ReadyTag })
            .AddCheck<PostgresReadinessHealthCheck>("postgres", tags: new[] { ReadyTag });

        return services;
    }

    public static WebApplication MapMessageBridgeHealthAndMetrics(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check =>
                string.Equals(check.Name, "rabbitmq", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(check.Name, "postgres", StringComparison.OrdinalIgnoreCase),
            ResponseWriter = WriteReadyHealthStatusOnly
        });

        var options = app.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        if (options.MetricsEndpointEnabled)
        {
            app.MapPrometheusScrapingEndpoint("/metrics");
        }

        return app;
    }

    public static ILoggingBuilder AddMessageBridgeOpenTelemetryLogging(
        this ILoggingBuilder logging,
        ObservabilityOptions options)
    {
        logging.AddOpenTelemetry(logOptions =>
        {
            logOptions.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName));
            logOptions.IncludeScopes = true;
            logOptions.IncludeFormattedMessage = true;

            if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            {
                logOptions.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = new Uri(options.OtlpEndpoint);
                });
            }
        });

        return logging;
    }

    private static void AddTracing(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddSource("MassTransit");

        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            return;

        tracing.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri(options.OtlpEndpoint);
        });
    }

    private static void AddMetrics(MeterProviderBuilder metrics, ObservabilityOptions options)
    {
        metrics.AddRuntimeInstrumentation();
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddPrometheusExporter();

        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            return;

        metrics.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri(options.OtlpEndpoint);
        });
    }

    private static async Task WriteReadyHealthStatusOnly(
        HttpContext context,
        HealthReport report)
    {
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString(),
                StringComparer.OrdinalIgnoreCase)
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}

public interface IRabbitMqReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public interface IPostgresReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public sealed class RabbitMqReadinessProbe : IRabbitMqReadinessProbe
{
    private readonly RabbitMqOptions _rabbitOptions;

    public RabbitMqReadinessProbe(IOptions<RabbitMqOptions> rabbitOptions)
    {
        _rabbitOptions = rabbitOptions.Value;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
        };

        if (_rabbitOptions.UsesConnectionString)
        {
            factory.Uri = new Uri(_rabbitOptions.ConnectionString!);
        }
        else
        {
            factory.HostName = _rabbitOptions.Host;
            factory.Port = _rabbitOptions.Port;
            factory.UserName = _rabbitOptions.Username!;
            factory.Password = _rabbitOptions.Password!;
            factory.VirtualHost = _rabbitOptions.VirtualHost;
            if (_rabbitOptions.UseSsl)
            {
                factory.Ssl.Enabled = true;
                factory.Ssl.Version = System.Security.Authentication.SslProtocols.Tls12;
            }
        }

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        return connection.IsOpen;
    }
}

public sealed class PostgresReadinessProbe : IPostgresReadinessProbe
{
    private readonly string? _connectionString;

    public PostgresReadinessProbe(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection");
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return false;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection.State == System.Data.ConnectionState.Open;
    }
}

public sealed class RabbitMqReadinessHealthCheck : IHealthCheck
{
    private readonly IRabbitMqReadinessProbe _probe;

    public RabbitMqReadinessHealthCheck(IRabbitMqReadinessProbe probe)
    {
        _probe = probe;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _probe.IsReadyAsync(cancellationToken))
            {
                return HealthCheckResult.Healthy();
            }

            return HealthCheckResult.Unhealthy("RabbitMQ check failed.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ check failed.");
        }
    }
}

public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly IPostgresReadinessProbe _probe;

    public PostgresReadinessHealthCheck(IPostgresReadinessProbe probe)
    {
        _probe = probe;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _probe.IsReadyAsync(cancellationToken))
            {
                return HealthCheckResult.Healthy();
            }

            return HealthCheckResult.Unhealthy("PostgreSQL check failed.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL check failed.");
        }
    }
}

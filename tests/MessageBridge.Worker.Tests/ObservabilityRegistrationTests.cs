using System.Net;
using System.Text.Json;
using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Worker.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Wolverine;
using Xunit;

namespace MessageBridge.Worker.Tests;

[Trait("Category", "Unit")]
public sealed class ObservabilityRegistrationTests
{
    [Fact]
    public void Options_Bind_And_Validate()
    {
        using var validProvider = CreateServices(
            new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = "MessageBridge.TestWorker"
            });

        var validOptions = validProvider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        validOptions.ServiceName.ShouldBe("MessageBridge.TestWorker");
    }

    [Fact]
    public void Options_Reject_Bad_ServiceName()
    {
        using var invalidProvider = CreateServices(
            new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = " "
            });

        Should.Throw<OptionsValidationException>(() =>
            invalidProvider.GetRequiredService<IOptions<ObservabilityOptions>>().Value);
    }

    [Fact]
    public void Registers_OpenTelemetry_Providers()
    {
        using var provider = CreateServices(new Dictionary<string, string?>());

        provider.GetService<TracerProvider>().ShouldNotBeNull();
        provider.GetService<MeterProvider>().ShouldNotBeNull();
        provider.GetServices<ILoggerProvider>()
            .Any(logger => logger is OpenTelemetryLoggerProvider)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Endpoint_Test_Host_Does_Not_Start_Worker_Messaging_Runtime()
    {
        await using var host = await ObservabilityTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "localhost",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest"
            },
            AddDependencyHealthProbes);

        host.Services
            .GetRequiredService<IServiceProviderIsService>()
            .IsService(typeof(IMessageBus))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Metrics_Endpoint_Gated_By_Config()
    {
        await using var disabled = await ObservabilityTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "localhost",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest"
            },
            AddDependencyHealthProbes);

        (await disabled.Client.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var enabled = await ObservabilityTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Observability:MetricsEndpointEnabled"] = "true",
                ["RabbitMq:Host"] = "localhost",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest"
            },
            AddDependencyHealthProbes);

        (await enabled.Client.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_Includes_Rabbit_And_Postgres_Without_Secrets()
    {
        await using var host = await ObservabilityTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "localhost",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
                ["Database:Host"] = "localhost",
                ["Database:Database"] = "messagebridge_dev",
                ["Database:Username"] = "db_user",
                ["Database:Password"] = "super_secret_pwd"
            },
            AddDependencyHealthProbes);

        var response = await host.Client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldNotContain("super_secret_pwd");

        using var document = JsonDocument.Parse(body);
        var checks = document.RootElement.GetProperty("checks");
        checks.GetProperty("rabbitmq").GetString().ShouldBe("Healthy");
        checks.GetProperty("postgres").GetString().ShouldBe("Healthy");
        checks.GetRawText().ShouldNotContain("description");
        checks.GetRawText().ShouldNotContain("exception");
    }

    [Fact]
    public void Consumer_Logs_Safe_Lifecycle_Metadata()
    {
        var message = new SendWhatsAppMessageCommand
        {
            MessageId = "message-001",
            TenantId = "tenant-1",
            TemplateName = "welcome",
            RecipientPhoneNumber = "+1 (555) 123-4567"
        };

        var metadata = ConsumerLifecycleMetadata.ForWhatsApp(message);

        metadata[ConsumerLifecycleMetadata.MessageIdKey].ShouldBe("message-001");
        metadata[ConsumerLifecycleMetadata.TenantIdKey].ShouldBe("tenant-1");
        metadata[ConsumerLifecycleMetadata.TemplateNameKey].ShouldBe("welcome");
        metadata[ConsumerLifecycleMetadata.RecipientKey].ShouldBe("*******4567");
        metadata.ShouldNotContainKey("TemplateParameters");

        var emailMessage = new SendEmailConfirmationCommand
        {
            MessageId = "message-002",
            TenantId = "tenant-1",
            RecipientEmail = "person@example.com"
        };

        var emailMetadata = ConsumerLifecycleMetadata.ForEmailConfirmation(emailMessage);
        emailMetadata[ConsumerLifecycleMetadata.RecipientKey].ShouldBe("p***n@***.com");
        emailMetadata[ConsumerLifecycleMetadata.TemplateNameKey].ShouldBe("confirm-email");
    }

    [Fact]
    public void ConsumerLifecycleMetadata_Excludes_Sensitive_Fields()
    {
        var message = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-sensitive",
            TenantId = "tenant-secure",
            TemplateName = "verify",
            TemplateLanguage = "en",
            TemplateParameters = { ["code"] = "123456", ["url"] = "https://verify.example.com/abc" },
            RecipientPhoneNumber = "+1 (555) 555-5555",
            CorrelationId = "corr-xyz"
        };

        var metadata = ConsumerLifecycleMetadata.ForWhatsApp(message);

        metadata.Keys.ShouldNotContain("TemplateParameters");
        metadata.Keys.ShouldNotContain("TemplateLanguage");
        var metadataStr = string.Join("|", metadata.Values);
        metadataStr.ShouldNotContain("123456");
        metadataStr.ShouldNotContain("https://verify");
    }

    [Fact]
    public void ConsumerLifecycleMetadata_Masks_Different_Email_Formats()
    {
        var addresses = new[]
        {
            "a@example.com",
            "test.user+tag@example.co.uk",
            "user123@sub.domain.example.org"
        };

        foreach (var addr in addresses)
        {
            var message = new SendEmailConfirmationCommand
            {
                MessageId = $"msg-{addresses.ToList().IndexOf(addr)}",
                TenantId = "tenant-1",
                RecipientEmail = addr,
                ConfirmationToken = "token"
            };

            var metadata = ConsumerLifecycleMetadata.ForEmailConfirmation(message);
            var masked = metadata[ConsumerLifecycleMetadata.RecipientKey]?.ToString() ?? string.Empty;

            masked.ShouldNotContain(addr);
            masked.ShouldContain("*");
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RabbitMqHealthCheck_maps_probe_result(bool ready, bool healthy)
    {
        using var tokenSource = new CancellationTokenSource();
        var probe = new ControllableRabbitProbe { Result = ready };
        var check = new RabbitMqReadinessHealthCheck(probe);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), tokenSource.Token);

        result.Status.ShouldBe(healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        probe.CancellationToken.ShouldBe(tokenSource.Token);
    }

    [Fact]
    public async Task RabbitMqHealthCheck_maps_probe_exception_to_safe_unhealthy_result()
    {
        var probe = new ControllableRabbitProbe
        {
            Exception = new InvalidOperationException("secret broker details")
        };

        var result = await new RabbitMqReadinessHealthCheck(probe)
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("RabbitMQ check failed.");
        (result.Description ?? string.Empty).ShouldNotContain("secret broker details");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PostgresHealthCheck_maps_probe_result(bool ready, bool healthy)
    {
        using var tokenSource = new CancellationTokenSource();
        var probe = new ControllablePostgresProbe { Result = ready };
        var check = new PostgresReadinessHealthCheck(probe);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), tokenSource.Token);

        result.Status.ShouldBe(healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        probe.CancellationToken.ShouldBe(tokenSource.Token);
    }

    [Fact]
    public async Task PostgresHealthCheck_maps_probe_exception_to_safe_unhealthy_result()
    {
        var probe = new ControllablePostgresProbe
        {
            Exception = new InvalidOperationException("secret database details")
        };

        var result = await new PostgresReadinessHealthCheck(probe)
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("PostgreSQL check failed.");
        (result.Description ?? string.Empty).ShouldNotContain("secret database details");
    }

    [Fact]
    public async Task PostgresReadinessProbe_uses_shared_data_source_and_honors_cancellation()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=unit-test;Database=bridge;Username=user;Password=password");
        var probe = new PostgresReadinessProbe(dataSource);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.IsReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task RabbitMqReadinessProbe_uses_connection_string_and_honors_cancellation()
    {
        var options = Options.Create(new RabbitMqOptions
        {
            ConnectionString = "amqps://guest:guest@unit-test"
        });
        var probe = new RabbitMqReadinessProbe(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.IsReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task RabbitMqReadinessProbe_configures_decomposed_tls_before_cancellation()
    {
        var options = Options.Create(new RabbitMqOptions
        {
            Host = "unit-test",
            Port = 5671,
            VirtualHost = "/secure",
            Username = "guest",
            Password = "guest",
            UseSsl = true
        });
        var probe = new RabbitMqReadinessProbe(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.IsReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task RabbitMqHealthCheck_forwards_a_cancelled_token()
    {
        var probe = new ControllableRabbitProbe { Result = false };
        var check = new RabbitMqReadinessHealthCheck(probe);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        probe.CancellationToken.ShouldBe(cancellation.Token);
        probe.CancellationToken.IsCancellationRequested.ShouldBeTrue();
    }

    private static ServiceProvider CreateServices(IDictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var observabilityOptions = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(observabilityOptions);

        services.AddMessageBridgeObservability(configuration);
        services.AddLogging(logging => logging.AddMessageBridgeOpenTelemetryLogging(observabilityOptions));
        AddDependencyHealthProbes(services);

        return services.BuildServiceProvider();
    }

    private static void AddDependencyHealthProbes(IServiceCollection services)
    {
        services.AddSingleton<IRabbitMqReadinessProbe, ReadyReadinessProbe>();
        services.AddSingleton<IPostgresReadinessProbe, ReadyReadinessProbe>();
        services.AddSingleton<IWhatsAppMessageSender, ReadyWhatsAppMessageSender>();
        services.AddSingleton<IEmailConfirmationSender, ReadyEmailConfirmationMessageSender>();
        services.AddSingleton<IMessageProcessingStore, ReadyMessageProcessingStore>();
        services.AddSingleton<ITenantConfigurationProvider, ReadyTenantConfigurationProvider>();
        services.AddSingleton<IProviderRateLimiter, ReadyProviderRateLimiter>();
    }

    private sealed class ReadyWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyEmailConfirmationMessageSender : IEmailConfirmationSender
    {
        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyMessageProcessingStore : IMessageProcessingStore
    {
        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyReadinessProbe : IRabbitMqReadinessProbe, IPostgresReadinessProbe
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ControllableRabbitProbe : IRabbitMqReadinessProbe
    {
        public bool Result { get; init; }
        public Exception? Exception { get; init; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            if (Exception is not null)
                throw Exception;

            return Task.FromResult(Result);
        }
    }

    private sealed class ControllablePostgresProbe : IPostgresReadinessProbe
    {
        public bool Result { get; init; }
        public Exception? Exception { get; init; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            if (Exception is not null)
                throw Exception;

            return Task.FromResult(Result);
        }
    }

    private sealed class ReadyTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId) =>
            Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, true));
    }

    private sealed class ReadyProviderRateLimiter : IProviderRateLimiter
    {
        public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ObservabilityTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ObservabilityTestHost(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public IServiceProvider Services => _app.Services;

        public static async Task<ObservabilityTestHost> StartAsync(
            IReadOnlyDictionary<string, string?> values,
            Action<IServiceCollection> configureServices)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(values);

            builder.Services.AddMessageBridgeObservability(builder.Configuration);
            configureServices(builder.Services);

            var app = builder.Build();
            app.MapMessageBridgeHealthAndMetrics();
            await app.StartAsync();

            return new ObservabilityTestHost(app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}

using System.Net;
using System.Text.Json;
using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Worker.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Wolverine;
using Xunit;

namespace MessageBridge.Worker.Tests;

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
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=messagebridge_dev;Username=db_user;Password=super_secret_pwd;"
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

using ErrorOr;
using MessageBridge.Application.Abstractions;
using System.Net;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using MessageBridge.Contracts.V1;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Mappers;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Worker.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Shouldly;
using Xunit;
using HandlerProcessingStore = MessageBridge.Application.Abstractions.IMessageProcessingStore;
using LifecycleProcessingStore = MessageBridge.Application.Persistence.IMessageProcessingStore;

namespace MessageBridge.Worker.Tests;

[Trait("Category", "Unit")]
public sealed class WorkerHostTests
{
    [Fact]
    public async Task Host_Maps_Only_Live_And_Ready_Health_Endpoints()
    {
        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(services));
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var readyResponse = await client.GetAsync("/health/ready");
        var readyBody = await readyResponse.Content.ReadAsStringAsync();
        readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK, readyBody);

        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Host_Registers_MassTransit_Consumers_And_Wolverine()
    {
        using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(services));
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetService<SendWhatsAppMessageConsumer>().ShouldNotBeNull();
        services.GetService<SendEmailConfirmationConsumer>().ShouldNotBeNull();
        services.GetService<IMessageBus>().ShouldNotBeNull();
        services.GetService<IOptions<RabbitMqOptions>>().ShouldNotBeNull();
    }

    [Fact]
    public void Contract_Maps_To_SendWhatsAppMessage_Command_With_Normalized_Values()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-1",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Alex" },
            CorrelationId = " ",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
        };

        var command = contract.ToApplicationCommand();

        command.MessageId.ShouldBe("msg-1");
        command.TemplateParameters.ShouldNotBeNull();
        command.CorrelationId.ShouldBeNull();
        command.RequestedAtUtc.ShouldBe(requestedAt);
        command.TemplateParameters!["name"].ShouldBe("Alex");
    }

    [Fact]
    public void Contract_Maps_To_SendEmailConfirmation_Command_With_Normalized_Values()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var expiresAt = requestedAt.AddHours(2);
        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "msg-2",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = string.Empty,
            ConfirmationToken = "token",
            CorrelationId = "",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAt),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
        };

        var command = contract.ToApplicationCommand();

        command.RecipientName.ShouldBeNull();
        command.CorrelationId.ShouldBeNull();
        command.ExpiresAtUtc.ShouldBe(expiresAt);
    }

    [Fact]
    public void Contract_WhatsApp_Preserves_TemplateParameters_When_Present()
    {
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-params",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "order_confirmed",
            TemplateLanguage = "en",
            TemplateParameters = { ["order_id"] = "ORD-123", ["amount"] = "$50.00" },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var command = contract.ToApplicationCommand();

        command.TemplateParameters.ShouldNotBeNull();
        command.TemplateParameters["order_id"].ShouldBe("ORD-123");
        command.TemplateParameters["amount"].ShouldBe("$50.00");
    }

    [Fact]
    public void Contract_Email_Preserves_RecipientName_When_Populated()
    {
        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "msg-name",
            TenantId = "tenant-1",
            RecipientEmail = "alice@example.com",
            RecipientName = "Alice Smith",
            ConfirmationToken = "token-123",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var command = contract.ToApplicationCommand();

        command.RecipientName.ShouldBe("Alice Smith");
        command.RecipientEmail.ShouldBe("alice@example.com");
    }

    [Fact]
    public void Contract_WhatsApp_Null_CorrelationId_WhenWhitespaceOnly()
    {
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-trim",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "alert",
            TemplateLanguage = "en",
            CorrelationId = "   ",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var command = contract.ToApplicationCommand();

        command.CorrelationId.ShouldBeNull();
    }

    [Fact]
    public void Contract_WhatsApp_Preserves_CorrelationId_When_NonWhitespace()
    {
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-corr-id",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "notify",
            TemplateLanguage = "en",
            CorrelationId = "corr-xyz-123",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var command = contract.ToApplicationCommand();

        command.CorrelationId.ShouldBe("corr-xyz-123");
    }

    [Fact]
    public void Contract_Email_Phone_Normalization_With_Various_Formats()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var phones = new[] { "+1 (555) 111-1111", "+44 207183 1132", "5551234567" };

        foreach (var phone in phones)
        {
            var contract = new SendWhatsAppMessageCommand
            {
                MessageId = $"msg-phone-{phones.ToList().IndexOf(phone)}",
                TenantId = "tenant-1",
                RecipientPhoneNumber = phone,
                TemplateName = "notify",
                TemplateLanguage = "en",
                RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
            };

            var command = contract.ToApplicationCommand();

            command.RecipientPhoneNumber.ShouldBe(phone);
            command.MessageId.ShouldBe(contract.MessageId);
        }
    }

    [Fact]
    public async Task Host_Boots_With_Empty_Tenant_Allowlist_And_Rejects_Tenant_Work()
    {
        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServicesWithoutTenantProvider(services));
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var tenantConfigProvider = scope.ServiceProvider.GetRequiredService<ITenantConfigurationProvider>();

        var result = await tenantConfigProvider.GetTenantConfigAsync("any-tenant");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public void Host_Fails_To_Start_With_Invalid_RabbitMq_Options()
    {
        using var factory = BuildWorkerFactory(
            new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "rabbitmq://bad-scheme"
            },
            services => AddTestRuntimeServices(services));

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Host_Fails_To_Start_When_Runtime_Dependencies_Are_Missing()
    {
        using var factory = BuildWorkerFactory(ValidRabbitMqSettings());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("Unable to resolve service for type");
        new[]
        {
            nameof(IWhatsAppMessageSender),
            nameof(IEmailConfirmationSender),
            nameof(ITenantConfigurationProvider),
            nameof(IProviderRateLimiter)
        }.Any(exception.Message.Contains).ShouldBeTrue(exception.Message);
    }

    private static MessageBridgeWorkerFactory BuildWorkerFactory(
        IReadOnlyDictionary<string, string?> values,
        Action<IServiceCollection>? configureServices = null)
        => new(values, configureServices);

    private static Dictionary<string, string?> ValidRabbitMqSettings() =>
        new()
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest",
            ["MessageBridge:ProcessingHistory:RecoveryEnabled"] = "false"
        };

    private static void AddTestRuntimeServices(IServiceCollection services)
    {
        AddTestRuntimeServicesWithoutTenantProvider(services);
        services.AddSingleton<ITenantConfigurationProvider, ReadyTenantConfigurationProvider>();
    }

    private static void AddTestRuntimeServicesWithoutTenantProvider(IServiceCollection services)
    {
        services.AddSingleton<IWhatsAppMessageSender, ReadyWhatsAppMessageSender>();
        services.AddSingleton<IEmailConfirmationSender, ReadyEmailConfirmationSender>();
        services.AddSingleton<HandlerProcessingStore, ReadyMessageProcessingStore>();
        services.AddSingleton<LifecycleProcessingStore, ReadyLifecycleProcessingStore>();
        services.AddSingleton<IProviderRateLimiter, ReadyProviderRateLimiter>();
        services.AddSingleton<IRabbitMqReadinessProbe, ReadyRabbitMqProbe>();
        services.AddSingleton<IPostgresReadinessProbe, ReadyPostgresProbe>();
    }

    private sealed class MessageBridgeWorkerFactory(
        IReadOnlyDictionary<string, string?> values,
        Action<IServiceCollection>? configureServices)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(values);
            });
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }

    private sealed class ReadyWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyEmailConfirmationSender : IEmailConfirmationSender
    {
        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyMessageProcessingStore : HandlerProcessingStore
    {
        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyLifecycleProcessingStore : LifecycleProcessingStore
    {
        public Task<CreateMessageProcessingResult> CreateAsync(
            CreateMessageProcessingRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CreateMessageProcessingResult(
                CreateMessageProcessingOutcome.Created,
                new MessageProcessingSnapshot(
                    Guid.NewGuid(), request.MessageId, request.MessageType,
                    ProcessingStatus.Received, request.PayloadHash, request.Provider,
                    new Dictionary<string, string?>(request.ProviderMetadata), null, 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

        public Task<MessageProcessingSnapshot?> GetAsync(
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default)
            => Task.FromResult<MessageProcessingSnapshot?>(null);

        public Task<MessageProcessingSnapshot> UpdateStatusAsync(
            string messageId,
            string messageType,
            ProcessingStatus status,
            string? failureReason = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MessageProcessingSnapshot(
                Guid.NewGuid(), messageId, messageType, status, string.Empty, string.Empty,
                new Dictionary<string, string?>(), failureReason, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                status is ProcessingStatus.Completed or ProcessingStatus.Failed
                    or ProcessingStatus.Abandoned or ProcessingStatus.Rejected
                    ? DateTimeOffset.UtcNow
                    : null));
    }

    private sealed class ReadyTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId)
            => Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, true));
    }

    private sealed class ReadyProviderRateLimiter : IProviderRateLimiter
    {
        public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class ReadyRabbitMqProbe : IRabbitMqReadinessProbe
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class ReadyPostgresProbe : IPostgresReadinessProbe
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

}

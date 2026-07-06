using ErrorOr;
using System.Net;
using Google.Protobuf.WellKnownTypes;
using MessageBridge.Application.Abstractions;
using MessageBridge.Contracts.V1;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Mappers;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Shouldly;
using Xunit;

namespace MessageBridge.Worker.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task Host_Maps_Only_Live_And_Ready_Health_Endpoints()
    {
        await using var factory = BuildWorkerFactory(ValidRabbitMqSettings());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Host_Registers_MassTransit_Consumers_And_Wolverine()
    {
        using var factory = BuildWorkerFactory(ValidRabbitMqSettings());
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
    public void Host_Fails_To_Start_With_Invalid_RabbitMq_Options()
    {
        using var factory = BuildWorkerFactory(new Dictionary<string, string?>
        {
            ["RabbitMq:ConnectionString"] = "rabbitmq://bad-scheme"
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Host_Maps_And_Dispatches_WhatsApp_MessageContract_To_Wolverine_Handler()
    {
        var sender = new TrackingWhatsAppMessageSender();
        var store = new TrackingMessageProcessingStore();
        var tenantConfigProvider = new TrackingTenantConfigurationProvider();
        var rateLimiter = new TrackingProviderRateLimiter();

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services =>
            {
                services.AddSingleton<IWhatsAppMessageSender>(sender);
                services.AddSingleton<IMessageProcessingStore>(store);
                services.AddSingleton<ITenantConfigurationProvider>(tenantConfigProvider);
                services.AddSingleton<IProviderRateLimiter>(rateLimiter);
            });

        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "message-001",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Ada" },
            CorrelationId = " ",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await messageBus.SendAsync(contract.ToApplicationCommand());
        client.ShouldNotBeNull();
        sender.Messages.Count.ShouldBe(1);
        store.RecordedMessageIds.ShouldContain("message-001");
        tenantConfigProvider.CallCount.ShouldBe(1);
        rateLimiter.CallCount.ShouldBe(1);
        sender.Messages[0].TemplateParameters.ShouldNotBeNull();
    }

    [Fact]
    public async Task Host_Maps_And_Dispatches_Email_MessageContract_To_Wolverine_Handler()
    {
        var sender = new TrackingEmailConfirmationSender();
        var store = new TrackingMessageProcessingStore();
        var tenantConfigProvider = new TrackingTenantConfigurationProvider();
        var rateLimiter = new TrackingProviderRateLimiter();

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services =>
            {
                services.AddSingleton<IEmailConfirmationSender>(sender);
                services.AddSingleton<IMessageProcessingStore>(store);
                services.AddSingleton<ITenantConfigurationProvider>(tenantConfigProvider);
                services.AddSingleton<IProviderRateLimiter>(rateLimiter);
            });

        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "message-002",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = "Alex",
            ConfirmationToken = "token-123",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            CorrelationId = " ",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await messageBus.SendAsync(contract.ToApplicationCommand());
        client.ShouldNotBeNull();
        sender.Emails.Count.ShouldBe(1);
        store.RecordedMessageIds.ShouldContain("message-002");
        tenantConfigProvider.CallCount.ShouldBe(1);
        rateLimiter.CallCount.ShouldBe(1);
        sender.Emails[0].RecipientEmailAddress.ShouldBe("user@example.com");
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
            ["RabbitMq:Password"] = "guest"
        };

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

    private sealed class TrackingWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public List<WhatsAppMessage> Messages { get; } = [];

        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId)
        {
            Messages.Add(message);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class TrackingEmailConfirmationSender : IEmailConfirmationSender
    {
        public List<EmailConfirmation> Emails { get; } = [];

        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId)
        {
            Emails.Add(email);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class TrackingMessageProcessingStore : IMessageProcessingStore
    {
        public List<string> RecordedMessageIds { get; } = [];

        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
        {
            RecordedMessageIds.Add(messageId);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class TrackingTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public int CallCount { get; private set; }

        public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId)
        {
            CallCount++;
            return Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, IsActive: true));
        }
    }

    private sealed class TrackingProviderRateLimiter : IProviderRateLimiter
    {
        public int CallCount { get; private set; }

        public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType)
        {
            CallCount++;
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }
}

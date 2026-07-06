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

    [Fact]
    public async Task Consumer_Maps_And_Dispatches_WhatsApp_MessageContract_To_Wolverine_Handler()
    {
        var sender = new TrackingWhatsAppMessageSender();
        var store = new TrackingMessageProcessingStore();
        var lifecycleStore = new TrackingLifecycleMessageProcessingStore();
        var tenantConfigProvider = new TrackingTenantConfigurationProvider();
        var rateLimiter = new TrackingProviderRateLimiter();

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(
                services,
                whatsAppSender: sender,
                store: store,
                lifecycleStore: lifecycleStore,
                tenantConfigProvider: tenantConfigProvider,
                rateLimiter: rateLimiter));

        using var scope = factory.Services.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<SendWhatsAppMessageConsumer>();

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

        await consumer.Consume(CreateConsumeContext(contract));
        sender.Messages.Count.ShouldBe(1);
        store.RecordedMessageIds.ShouldContain("message-001");
        lifecycleStore.GetRequired("message-001", nameof(SendWhatsAppMessageCommand)).Status.ShouldBe(ProcessingStatus.Completed);
        tenantConfigProvider.CallCount.ShouldBe(1);
        rateLimiter.CallCount.ShouldBe(1);
        sender.Messages[0].TemplateParameters.ShouldNotBeNull();
    }

    [Fact]
    public async Task Consumer_Maps_And_Dispatches_Email_MessageContract_To_Wolverine_Handler()
    {
        var sender = new TrackingEmailConfirmationSender();
        var store = new TrackingMessageProcessingStore();
        var lifecycleStore = new TrackingLifecycleMessageProcessingStore();
        var tenantConfigProvider = new TrackingTenantConfigurationProvider();
        var rateLimiter = new TrackingProviderRateLimiter();

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(
                services,
                emailSender: sender,
                store: store,
                lifecycleStore: lifecycleStore,
                tenantConfigProvider: tenantConfigProvider,
                rateLimiter: rateLimiter));

        using var scope = factory.Services.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<SendEmailConfirmationConsumer>();

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

        await consumer.Consume(CreateConsumeContext(contract));
        sender.Emails.Count.ShouldBe(1);
        store.RecordedMessageIds.ShouldContain("message-002");
        lifecycleStore.GetRequired("message-002", nameof(SendEmailConfirmationCommand)).Status.ShouldBe(ProcessingStatus.Completed);
        tenantConfigProvider.CallCount.ShouldBe(1);
        rateLimiter.CallCount.ShouldBe(1);
        sender.Emails[0].RecipientEmailAddress.ShouldBe("user@example.com");
    }

    [Fact]
    public async Task Consumer_Fails_WhatsApp_Delivery_When_Handler_Returns_Error()
    {
        var sender = new TrackingWhatsAppMessageSender
        {
            Result = Error.Failure(
                "Provider.Send",
                "token=abcdefghijklmnopqrstuvwxyz phone=+1 (415) 555-2671")
        };

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(services, whatsAppSender: sender));

        using var scope = factory.Services.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<SendWhatsAppMessageConsumer>();

        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "message-003",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => consumer.Consume(CreateConsumeContext(contract)));

        exception.Message.ShouldContain("SendWhatsAppMessageCommand");
        exception.Message.ShouldContain("*******2671");
        exception.Message.ShouldNotContain("abcdefghijklmnopqrstuvwxyz");
    }

    [Fact]
    public async Task Consumer_Fails_Email_Delivery_When_Handler_Returns_Error()
    {
        var sender = new TrackingEmailConfirmationSender
        {
            Result = Error.Failure(
                "Provider.Send",
                "confirmation_token=super_secret_token_value user=person@example.com")
        };

        await using var factory = BuildWorkerFactory(
            ValidRabbitMqSettings(),
            services => AddTestRuntimeServices(services, emailSender: sender));

        using var scope = factory.Services.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<SendEmailConfirmationConsumer>();

        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "message-004",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "token-123",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => consumer.Consume(CreateConsumeContext(contract)));

        exception.Message.ShouldContain("SendEmailConfirmationCommand");
        exception.Message.ShouldContain("p***n@***.com");
        exception.Message.ShouldNotContain("super_secret_token_value");
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

    private static ConsumeContext<TMessage> CreateConsumeContext<TMessage>(TMessage message)
        where TMessage : class
        => ConsumeContextProxy<TMessage>.Create(message);

    private static void AddTestRuntimeServices(
        IServiceCollection services,
        IWhatsAppMessageSender? whatsAppSender = null,
        IEmailConfirmationSender? emailSender = null,
        HandlerProcessingStore? store = null,
        LifecycleProcessingStore? lifecycleStore = null,
        ITenantConfigurationProvider? tenantConfigProvider = null,
        IProviderRateLimiter? rateLimiter = null)
    {
        services.AddSingleton<IWhatsAppMessageSender>(whatsAppSender ?? new TrackingWhatsAppMessageSender());
        services.AddSingleton<IEmailConfirmationSender>(emailSender ?? new TrackingEmailConfirmationSender());
        services.AddSingleton<HandlerProcessingStore>(store ?? new TrackingMessageProcessingStore());
        services.AddSingleton<LifecycleProcessingStore>(lifecycleStore ?? new TrackingLifecycleMessageProcessingStore());
        services.AddSingleton<ITenantConfigurationProvider>(tenantConfigProvider ?? new TrackingTenantConfigurationProvider());
        services.AddSingleton<IProviderRateLimiter>(rateLimiter ?? new TrackingProviderRateLimiter());
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

    private sealed class TrackingWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public List<WhatsAppMessage> Messages { get; } = [];
        public ErrorOr<Success> Result { get; set; } = new Success();

        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }
    }

    private sealed class TrackingEmailConfirmationSender : IEmailConfirmationSender
    {
        public List<EmailConfirmation> Emails { get; } = [];
        public ErrorOr<Success> Result { get; set; } = new Success();

        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId)
        {
            Emails.Add(email);
            return Task.FromResult(Result);
        }
    }

    private sealed class TrackingMessageProcessingStore : HandlerProcessingStore
    {
        public List<string> RecordedMessageIds { get; } = [];

        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
        {
            RecordedMessageIds.Add(messageId);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class TrackingLifecycleMessageProcessingStore : LifecycleProcessingStore
    {
        private readonly Dictionary<(string MessageId, string MessageType), MessageProcessingSnapshot> _records = [];

        public Task<CreateMessageProcessingResult> CreateAsync(
            CreateMessageProcessingRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = (request.MessageId, request.MessageType);
            if (_records.TryGetValue(key, out var existing))
            {
                return Task.FromResult(
                    new CreateMessageProcessingResult(CreateMessageProcessingOutcome.Duplicate, existing));
            }

            var now = DateTimeOffset.UtcNow;
            var created = new MessageProcessingSnapshot(
                Guid.NewGuid(),
                request.MessageId,
                request.MessageType,
                ProcessingStatus.Received,
                request.PayloadHash,
                request.Provider,
                new Dictionary<string, string?>(request.ProviderMetadata),
                null,
                1,
                now,
                now,
                null);
            _records[key] = created;
            return Task.FromResult(
                new CreateMessageProcessingResult(CreateMessageProcessingOutcome.Created, created));
        }

        public Task<MessageProcessingSnapshot?> GetAsync(
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            _records.TryGetValue((messageId, messageType), out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<MessageProcessingSnapshot> UpdateStatusAsync(
            string messageId,
            string messageType,
            ProcessingStatus status,
            string? failureReason = null,
            CancellationToken cancellationToken = default)
        {
            var current = GetRequired(messageId, messageType);
            var updatedAt = DateTimeOffset.UtcNow;
            var updated = current with
            {
                Status = status,
                FailureReason = failureReason,
                UpdatedAt = updatedAt,
                ProcessedAt = status is ProcessingStatus.Completed
                    or ProcessingStatus.Failed
                    or ProcessingStatus.Abandoned
                    or ProcessingStatus.Rejected
                    ? updatedAt
                    : null
            };
            _records[(messageId, messageType)] = updated;
            return Task.FromResult(updated);
        }

        public MessageProcessingSnapshot GetRequired(string messageId, string messageType)
        {
            return _records[(messageId, messageType)];
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

    private class ConsumeContextProxy<TMessage> : DispatchProxy
        where TMessage : class
    {
        private TMessage? _message;

        public static ConsumeContext<TMessage> Create(TMessage message)
        {
            var proxy = Create<ConsumeContext<TMessage>, ConsumeContextProxy<TMessage>>();
            ((ConsumeContextProxy<TMessage>)(object)proxy)._message = message;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Message" => _message!,
                "get_CancellationToken" => CancellationToken.None,
                _ => throw new NotSupportedException(
                    $"Unexpected ConsumeContext member: {targetMethod?.Name}")
            };
    }
}

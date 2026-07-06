using FluentAssertions;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.Publisher;
using MessageBridge.Publisher.Requests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

public sealed class RabbitMqPublishConsumeTests : IAsyncLifetime
{
    private readonly RabbitMqFixture _rabbitMqFixture = new();
    private PostgresFixture? _postgresFixture;
    private MessageBridgeDbContext? _dbContext;
    private AsyncServiceScope _scope;
    private ServiceProvider? _serviceProvider;

    public async Task InitializeAsync()
    {
        await _rabbitMqFixture.InitializeAsync();
        _postgresFixture = new PostgresFixture();
        await _postgresFixture.InitializeAsync();
        _dbContext = await _postgresFixture.CreateDbContextAsync();

        var services = new ServiceCollection();
        _rabbitMqFixture.RegisterServices(services, _dbContext, ConfigureTestConsumers);
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateAsyncScope();

        // Start the MassTransit bus so consumers can receive messages
        var busControl = _scope.ServiceProvider.GetRequiredService<IBus>() as IBusControl;
        await busControl!.StartAsync(TimeSpan.FromSeconds(10));
    }

    public async Task DisposeAsync()
    {
        try
        {
            var busControl = _scope.ServiceProvider.GetRequiredService<IBus>() as IBusControl;
            await busControl?.StopAsync(TimeSpan.FromSeconds(10))!;
        }
        catch
        {
            // Ignore if bus was not started
        }

        await _scope.DisposeAsync();

        if (_dbContext is not null)
        {
            await _dbContext.DisposeAsync();
        }

        if (_postgresFixture is not null)
        {
            await _postgresFixture.DisposeAsync();
        }

        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        await _rabbitMqFixture.DisposeAsync();
    }

    [Fact]
    public async Task PublishWhatsAppMessage_RoutsToConsumerAndStoresHistory()
    {
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var cmd = new SendWhatsAppMessageCommand
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientPhoneNumber = "+11234567890",
            TemplateName = "hello",
            TemplateParameters = { ["body"] = "test message" }
        };

        // Publish command
        await bus.Publish(cmd);

        // Poll until record is verified (proves consumer processed routing)
        await IntegrationTestsHelper.PollUntilAsync(
            async () =>
            {
                var record = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
                return record is not null && record.Status == ProcessingStatus.Completed;
            },
            TimeSpan.FromSeconds(10));

        // Verify record exists
        var stored = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
        stored.Should().NotBeNull();
        stored!.MessageId.Should().Be(cmd.MessageId);
        stored.MessageType.Should().Be(nameof(SendWhatsAppMessageCommand));
        stored.Status.Should().Be(ProcessingStatus.Completed);
    }

    [Fact]
    public async Task PublishEmailConfirmationCommand_RoutsToConsumer()
    {
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var cmd = new SendEmailConfirmationCommand
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "token123"
        };

        await bus.Publish(cmd);

        // Poll until record is verified (proves consumer processed routing)
        await IntegrationTestsHelper.PollUntilAsync(
            async () =>
            {
                var record = await store.GetAsync(cmd.MessageId, nameof(SendEmailConfirmationCommand));
                return record is not null && record.Status == ProcessingStatus.Completed;
            },
            TimeSpan.FromSeconds(10));

        var stored = await store.GetAsync(cmd.MessageId, nameof(SendEmailConfirmationCommand));
        stored.Should().NotBeNull();
        stored!.MessageType.Should().Be(nameof(SendEmailConfirmationCommand));
        stored!.Status.Should().Be(ProcessingStatus.Completed);
    }

    private static void ConfigureTestConsumers(IBusRegistrationConfigurator config)
    {
        config.AddConsumer<RabbitMqWhatsAppPublishConsumer>();
        config.AddConsumer<RabbitMqEmailPublishConsumer>();
    }
}

internal sealed class RabbitMqWhatsAppPublishConsumer(
    IMessageProcessingStore store) : IConsumer<SendWhatsAppMessageCommand>
{
    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        var payloadHash = MessageProcessingTestHelpers.GetPayloadHash(context.Message);
        await MessageProcessingTestHelpers.EnsureRecordAsync(
            store,
            context.Message.MessageId,
            nameof(SendWhatsAppMessageCommand),
            payloadHash,
            "routed");

        await store.UpdateStatusAsync(
            context.Message.MessageId,
            nameof(SendWhatsAppMessageCommand),
            ProcessingStatus.Completed,
            "consumer_completed");
    }
}

internal sealed class RabbitMqEmailPublishConsumer(
    IMessageProcessingStore store) : IConsumer<SendEmailConfirmationCommand>
{
    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        var payloadHash = MessageProcessingTestHelpers.GetPayloadHash(context.Message);
        await MessageProcessingTestHelpers.EnsureRecordAsync(
            store,
            context.Message.MessageId,
            nameof(SendEmailConfirmationCommand),
            payloadHash,
            "routed");

        await store.UpdateStatusAsync(
            context.Message.MessageId,
            nameof(SendEmailConfirmationCommand),
            ProcessingStatus.Completed,
            "consumer_completed");
    }
}

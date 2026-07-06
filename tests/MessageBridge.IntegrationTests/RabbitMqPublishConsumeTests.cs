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
        _rabbitMqFixture.RegisterServices(services, _dbContext);
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateAsyncScope();
    }

    public async Task DisposeAsync()
    {
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

        var hash = GetPayloadHash(cmd);

        // Create processing record before publish
        var createReq = new CreateMessageProcessingRequest(
            cmd.MessageId,
            nameof(SendWhatsAppMessageCommand),
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["routed"] = "true" });

        var createRes = await store.CreateAsync(createReq);
        createRes.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);

        // Publish command
        await bus.Publish(cmd);

        // Give consumer time to process
        await Task.Delay(1000);

        // Verify record still exists
        var stored = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
        stored.Should().NotBeNull();
        stored!.MessageId.Should().Be(cmd.MessageId);
        stored.MessageType.Should().Be(nameof(SendWhatsAppMessageCommand));
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

        var hash = GetPayloadHash(cmd);

        var createReq = new CreateMessageProcessingRequest(
            cmd.MessageId,
            nameof(SendEmailConfirmationCommand),
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["routed"] = "true" });

        var createRes = await store.CreateAsync(createReq);
        createRes.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);

        await bus.Publish(cmd);
        await Task.Delay(1000);

        var stored = await store.GetAsync(cmd.MessageId, nameof(SendEmailConfirmationCommand));
        stored.Should().NotBeNull();
        stored!.MessageType.Should().Be(nameof(SendEmailConfirmationCommand));
    }

    private static string GetPayloadHash<T>(T msg)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(msg);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}

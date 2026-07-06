using FluentAssertions;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

public sealed class OutboxDispatchIntegrationTests : IAsyncLifetime
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
    public async Task PublishMessage_CreatesProcessingRecord_ForOutboxTracking()
    {
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var cmd = new SendWhatsAppMessageCommand
        {
            MessageId = $"outbox-msg-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientPhoneNumber = "+11234567890",
            TemplateName = "test",
            TemplateParameters = { ["body"] = "outbox test" }
        };

        var hash = GetPayloadHash(cmd);

        // Create outbox record
        var createReq = new CreateMessageProcessingRequest(
            cmd.MessageId,
            nameof(SendWhatsAppMessageCommand),
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["outbox"] = "true", ["attempt"] = "1" });

        var createRes = await store.CreateAsync(createReq);
        createRes.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);

        // Publish message
        await bus.Publish(cmd);
        await Task.Delay(500);

        // Verify record persisted
        var record = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
        record.Should().NotBeNull();
        record!.Provider.Should().Be("masstransit");
        record.ProviderMetadata["outbox"].Should().Be("true");
    }

    [Fact]
    public async Task DuplicateOutboxEntry_ReturnsExistingRecord_PreventingDoublePublish()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"dup-outbox-{Guid.NewGuid():N}";
        var hash = "hash-duplicate-outbox";

        var req = new CreateMessageProcessingRequest(
            msgId,
            "whatsapp.send",
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["duplicate"] = "true" });

        var first = await store.CreateAsync(req);
        var second = await store.CreateAsync(req);

        first.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        second.Outcome.Should().Be(CreateMessageProcessingOutcome.Duplicate);
        first.Record.Id.Should().Be(second.Record.Id);
    }

    [Fact]
    public async Task OutboxRecord_UpdatesStatusAfterDispatch()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"status-outbox-{Guid.NewGuid():N}";
        var msgType = "email.confirm";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-status",
            "masstransit",
            new Dictionary<string, string?> { ["status_test"] = "true" });

        var created = await store.CreateAsync(req);
        created.Record.Status.Should().Be(ProcessingStatus.Received);

        // Simulate dispatch completion
        var updated = await store.UpdateStatusAsync(
            msgId,
            msgType,
            ProcessingStatus.Completed);

        updated.Status.Should().Be(ProcessingStatus.Completed);
        updated.ProcessedAt.Should().NotBeNull();

        // Verify persistence
        var final = await store.GetAsync(msgId, msgType);
        final!.Status.Should().Be(ProcessingStatus.Completed);
    }

    private static string GetPayloadHash<T>(T msg)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(msg);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}

using FluentAssertions;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class OutboxDispatchIntegrationTests(IntegrationEnvironmentFixture fixture)
{
    [Fact]
    public async Task Committed_transaction_is_invisible_until_commit_then_dispatches_and_marks_published()
    {
        await using var scenario = await OutboxDispatchScenario.CreateAsync(fixture, failFirst: false);
        var marker = new OutboxBusinessMarker { Id = Guid.NewGuid(), Value = "committed" };
        var message = CreateMessage();

        await using var context = scenario.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.BusinessMarkers.Add(marker);
        await new MessageBridgeOutboxWriter(context).WriteAsync(message);
        await context.SaveChangesAsync();
        await scenario.StartDispatcherAsync();

        await scenario.AssertRowsInvisibleAsync(marker.Id, message.Id);
        await scenario.Probe.AssertNoMessagesAsync(message.MessageId);

        await transaction.CommitAsync();
        await scenario.WaitForRowsAsync(marker.Id, message.Id);
        await scenario.WaitForAttemptAsync(1);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowPublication();
        await scenario.Transport.WaitForSuccessfulPublicationAsync();
        var envelope = await scenario.Probe.WaitForMessageAsync(message.MessageId);
        envelope.ExchangeName.Should().Be(message.ExchangeName);
        envelope.RoutingKey.Should().Be(message.RoutingKey);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowDispatcherCompletion();
        (await scenario.WaitForPublishedAsync(message.Id)).PublishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Transient_publish_failure_retries_then_dispatches_the_row_once()
    {
        await using var scenario = await OutboxDispatchScenario.CreateAsync(fixture, failFirst: true);
        var marker = new OutboxBusinessMarker { Id = Guid.NewGuid(), Value = "retry" };
        var message = CreateMessage();

        await using (var context = scenario.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.BusinessMarkers.Add(marker);
            await new MessageBridgeOutboxWriter(context).WriteAsync(message);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await scenario.StartDispatcherAsync();
        await scenario.Transport.WaitForFirstFailureAsync();
        await scenario.WaitForAttemptAsync(2);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();
        await scenario.Probe.AssertNoMessagesAsync(message.MessageId);

        scenario.Transport.AllowPublication();
        await scenario.Transport.WaitForSuccessfulPublicationAsync();
        await scenario.Probe.WaitForMessageAsync(message.MessageId);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowDispatcherCompletion();
        (await scenario.WaitForPublishedAsync(message.Id)).PublishedAtUtc.Should().NotBeNull();
        await scenario.AssertNoDuplicateDispatchAsync(message.MessageId, expectedAttempts: 2);
    }

    private static MessageBridgeOutboxMessage CreateMessage() => new()
    {
        Id = $"outbox-{Guid.NewGuid():N}",
        MessageId = $"message-{Guid.NewGuid():N}",
        CorrelationId = $"correlation-{Guid.NewGuid():N}",
        ExchangeName = "messagebridge.integration",
        RoutingKey = "outbox.dispatch",
        Headers = """{"content-type":"application/json"}""",
        Payload = [1, 2, 3, 4],
        CreatedAtUtc = DateTime.UtcNow,
    };
}

internal sealed class OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options)
    : DbContext(options)
{
    public DbSet<OutboxBusinessMarker> BusinessMarkers => Set<OutboxBusinessMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxBusinessMarker>(builder =>
        {
            builder.ToTable("OutboxBusinessMarkers");
            builder.HasKey(marker => marker.Id);
            builder.Property(marker => marker.Value).IsRequired();
        });
        modelBuilder.ConfigureMessageBridgeOutbox();
    }
}

internal sealed class OutboxBusinessMarker
{
    public Guid Id { get; set; }
    public string Value { get; set; } = string.Empty;
}

public sealed class ProcessingTrackingIntegrationTests : IAsyncLifetime
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

        // Verify record persisted (poll until found or timeout)
        await IntegrationTestsHelper.PollUntilAsync(
            async () => await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand)) != null,
            TimeSpan.FromSeconds(10));

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

using FluentAssertions;
using System.Collections.Concurrent;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConsumerRetryAndRejectionTests(IntegrationEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;
    private MessageBridgeDbContext? _dbContext;
    private string? _databaseName;
    private string? _topologyPrefix;
    private AsyncServiceScope _scope;
    private ServiceProvider? _serviceProvider;
    private readonly ConsumerAttemptTracker _attemptTracker = new();

    public async Task InitializeAsync()
    {
        (_dbContext, _databaseName) = await _fixture.CreateMigratedDatabaseAsync();

        var services = new ServiceCollection();
        services.AddSingleton(_attemptTracker);
        _topologyPrefix = RegisterTestConsumers(services, _dbContext);
        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateAsyncScope();
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

        if (_databaseName is not null)
        {
            await _fixture.DropDatabaseAsync(_databaseName);
        }

        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    [Fact]
    public async Task MessageRetry_RecordTransitionsToCompleted_AfterTransientFailure()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();

        var cmd = new SendWhatsAppMessageCommand
        {
            MessageId = $"retry-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientPhoneNumber = "+11234567890",
            TemplateName = "retry",
            TemplateParameters = { ["body"] = "retry test" }
        };

        _attemptTracker.Reset(cmd.MessageId);
        await bus.Publish(cmd);

        await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () => await HasStatusAsync(store, cmd.MessageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed),
            "Retry record did not transition to completed.");

        var stored = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ProcessingStatus.Completed);
        _attemptTracker.GetAttemptCount(cmd.MessageId).Should().Be(2);
    }

    [Fact]
    public async Task MessageRejection_RecordTransitionsToFailed_AfterRetryExhaustion()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();

        var cmd = new SendEmailConfirmationCommand
        {
            MessageId = $"reject-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientEmail = "user@example.com",
            ConfirmationToken = "reject-token"
        };

        _attemptTracker.Reset(cmd.MessageId);
        await bus.Publish(cmd);

        await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () => await HasStatusAsync(store, cmd.MessageId, nameof(SendEmailConfirmationCommand), ProcessingStatus.Failed),
            "Rejected record did not transition to failed.");

        var stored = await store.GetAsync(cmd.MessageId, nameof(SendEmailConfirmationCommand));
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ProcessingStatus.Failed);
        stored.FailureReason.Should().NotBeNullOrWhiteSpace();
        _attemptTracker.GetAttemptCount(cmd.MessageId).Should().Be(2);
        await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () => await _fixture.GetQueueDepthAsync($"{_topologyPrefix}-rejecting-email_error") == 1,
            "Rejected message did not reach the error queue.");
    }

    private string RegisterTestConsumers(IServiceCollection services, MessageBridgeDbContext dbContext)
    {
        var connectionString = dbContext.Database.GetConnectionString()
            ?? dbContext.Database.GetDbConnection().ConnectionString;

        services.AddScoped<MessageBridgeDbContext>(_ => new MessageBridgeDbContext(
            new DbContextOptionsBuilder<MessageBridgeDbContext>()
                .UseNpgsql(connectionString)
                .Options));
        services.AddScoped<IMessageProcessingStore, MessageProcessingStore>();
        var topologyPrefix = IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix();
        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(
                topologyPrefix,
                includeNamespace: false));
            bus.AddConsumer<TransientRetryWhatsAppConsumer>(
                cfg => cfg.UseMessageRetry(r => r.Immediate(1)));
            bus.AddConsumer<RejectingEmailConsumer>(
                cfg => cfg.UseMessageRetry(r => r.Immediate(1)));
            bus.AddConsumer<RejectingEmailFaultConsumer>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(_fixture.GetRabbitMqConnectionString());
                cfg.ConfigureEndpoints(context);
            });
        });
        return topologyPrefix;
    }

    private static async Task<bool> HasStatusAsync(
        IMessageProcessingStore store,
        string messageId,
        string messageType,
        ProcessingStatus expectedStatus)
    {
        var record = await store.GetAsync(messageId, messageType);
        return record?.Status == expectedStatus;
    }

}

internal sealed class ConsumerAttemptTracker
{
    private readonly ConcurrentDictionary<string, int> _attempts = [];

    public void Reset(string messageId)
    {
        _attempts.TryRemove(messageId, out _);
    }

    public int GetNextAttempt(string messageId)
    {
        return _attempts.AddOrUpdate(messageId, 1, (_, attempts) => attempts + 1);
    }

    public int GetAttemptCount(string messageId)
    {
        return _attempts.TryGetValue(messageId, out var attempts) ? attempts : 0;
    }
}

internal sealed class TransientRetryWhatsAppConsumer(
    IMessageProcessingStore store,
    ConsumerAttemptTracker retryTracker) : IConsumer<SendWhatsAppMessageCommand>
{
    public async Task Consume(ConsumeContext<SendWhatsAppMessageCommand> context)
    {
        var attempt = retryTracker.GetNextAttempt(context.Message.MessageId);
        var payloadHash = MessageProcessingTestHelpers.GetPayloadHash(context.Message);

        await MessageProcessingTestHelpers.EnsureRecordAsync(
            store,
            context.Message.MessageId,
            nameof(SendWhatsAppMessageCommand),
            payloadHash,
            "retry");

        if (attempt == 1)
        {
            await store.UpdateStatusAsync(context.Message.MessageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Processing);
            throw new InvalidOperationException("Transient consumer failure for retry coverage.");
        }

        await store.UpdateStatusAsync(context.Message.MessageId, nameof(SendWhatsAppMessageCommand), ProcessingStatus.Completed);
    }
}

internal sealed class RejectingEmailConsumer(
    IMessageProcessingStore store,
    ConsumerAttemptTracker rejectionTracker) : IConsumer<SendEmailConfirmationCommand>
{
    public async Task Consume(ConsumeContext<SendEmailConfirmationCommand> context)
    {
        var attempt = rejectionTracker.GetNextAttempt(context.Message.MessageId);
        var payloadHash = MessageProcessingTestHelpers.GetPayloadHash(context.Message);

        await MessageProcessingTestHelpers.EnsureRecordAsync(
            store,
            context.Message.MessageId,
            nameof(SendEmailConfirmationCommand),
            payloadHash,
            "rejection");

        await store.UpdateStatusAsync(context.Message.MessageId, nameof(SendEmailConfirmationCommand), ProcessingStatus.Processing);
        throw new InvalidOperationException($"Transient consumer failure on attempt {attempt} for rejection coverage.");
    }
}

internal sealed class RejectingEmailFaultConsumer(IMessageProcessingStore store)
    : IConsumer<Fault<SendEmailConfirmationCommand>>
{
    public Task Consume(ConsumeContext<Fault<SendEmailConfirmationCommand>> context)
    {
        var failure = context.Message.Exceptions.FirstOrDefault()?.Message ?? "retry attempts exhausted";
        return store.UpdateStatusAsync(
            context.Message.Message.MessageId,
            nameof(SendEmailConfirmationCommand),
            ProcessingStatus.Failed,
            failure);
    }
}

internal static class MessageProcessingTestHelpers
{
    internal static async Task EnsureRecordAsync(
        IMessageProcessingStore store,
        string messageId,
        string messageType,
        string messageHash,
        string metadataValue)
    {
        var request = BuildCreateRequest(messageId, messageType, messageHash, metadataValue);
        await store.CreateAsync(request);
    }

    internal static string GetPayloadHash<T>(T message)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static CreateMessageProcessingRequest BuildCreateRequest(
        string messageId,
        string messageType,
        string payloadHash,
        string metadataValue)
    {
        return new CreateMessageProcessingRequest(
            messageId,
            messageType,
            payloadHash,
            "masstransit",
            new Dictionary<string, string?> { ["integration_test"] = metadataValue });
    }
}

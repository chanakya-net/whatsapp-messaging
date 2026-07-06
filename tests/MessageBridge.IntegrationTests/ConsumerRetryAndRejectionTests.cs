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

public sealed class ConsumerRetryAndRejectionTests : IAsyncLifetime
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
    public async Task MessageRetry_RecordIsMarkedAsReceived_BeforeRetry()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"retry-{Guid.NewGuid():N}";
        var msgType = nameof(SendWhatsAppMessageCommand);

        // Create record that will be retried
        var createReq = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-retry",
            "masstransit",
            new Dictionary<string, string?> { ["retry_count"] = "0" });

        var created = await store.CreateAsync(createReq);
        created.Record.Status.Should().Be(ProcessingStatus.Received);

        // Simulate retry by updating status to Processing (mid-retry)
        var processing = await store.UpdateStatusAsync(msgId, msgType, ProcessingStatus.Processing);
        processing.Status.Should().Be(ProcessingStatus.Processing);
    }

    [Fact]
    public async Task MessageRejection_RecordIsMarkedAsFailed_AfterRetryExhaustion()
    {
        var store = _scope.ServiceProvider.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"reject-{Guid.NewGuid():N}";
        var msgType = nameof(SendEmailConfirmationCommand);

        // Create record for message that will be rejected
        var createReq = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-reject",
            "masstransit",
            new Dictionary<string, string?> { ["retry_exhausted"] = "true" });

        var created = await store.CreateAsync(createReq);
        created.Record.Status.Should().Be(ProcessingStatus.Received);

        // Simulate rejection after retry exhaustion
        var rejected = await store.UpdateStatusAsync(msgId, msgType, ProcessingStatus.Failed);
        rejected.Status.Should().Be(ProcessingStatus.Failed);
        rejected.ProcessedAt.Should().NotBeNull("failed records should be marked with completion time");
    }
}

internal static class IntegrationTestsHelper
{
    internal static async Task PollUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(interval);
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds}s");
    }
}


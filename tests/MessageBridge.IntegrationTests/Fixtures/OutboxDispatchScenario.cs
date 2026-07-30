using FluentAssertions;
using MassTransit;
using MessageBridge.Publisher;
using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using MessageBridge.Publisher.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageBridge.IntegrationTests.Fixtures;

internal sealed class OutboxDispatchScenario : IAsyncDisposable
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(750);
    private readonly IntegrationEnvironmentFixture _fixture;
    private readonly string _databaseName;
    private readonly ServiceProvider _provider;
    private readonly IBusControl _bus;
    private readonly OutboxTestDbContextFactory _contextFactory;
    private readonly MessageBridgeOutboxDispatcherHostedService<OutboxTestDbContext> _dispatcher;
    private bool _dispatcherStarted;

    private OutboxDispatchScenario(
        IntegrationEnvironmentFixture fixture,
        string databaseName,
        ServiceProvider provider,
        IBusControl bus,
        OutboxTestDbContextFactory contextFactory,
        RabbitMqEnvelopeProbe probe,
        GatedPublishTransport transport)
    {
        _fixture = fixture;
        _databaseName = databaseName;
        _provider = provider;
        _bus = bus;
        _contextFactory = contextFactory;
        Probe = probe;
        Transport = transport;
        _dispatcher = CreateDispatcher(contextFactory, transport);
    }

    public RabbitMqEnvelopeProbe Probe { get; }
    public GatedPublishTransport Transport { get; }

    public static async Task<OutboxDispatchScenario> CreateAsync(
        IntegrationEnvironmentFixture fixture,
        bool failFirst)
    {
        var (connectionString, databaseName) = await fixture.CreateDatabaseAsync();
        try
        {
            var contextFactory = new OutboxTestDbContextFactory(connectionString);
            await EnsureSchemaCreatedAsync(contextFactory);
            return await StartInfrastructureAsync(
                fixture,
                databaseName,
                contextFactory,
                failFirst);
        }
        catch
        {
            await fixture.DropDatabaseAsync(databaseName);
            throw;
        }
    }

    public OutboxTestDbContext CreateDbContext() => _contextFactory.CreateDbContext();

    public async Task StartDispatcherAsync()
    {
        await _dispatcher.StartAsync(CancellationToken.None);
        _dispatcherStarted = true;
    }

    public Task AssertRowsInvisibleAsync(Guid markerId, string outboxId) =>
        IntegrationEnvironmentFixture.AssertRemainsAsync(
            async () =>
            {
                await using var context = CreateDbContext();
                var markerVisible = await context.BusinessMarkers.AnyAsync(x => x.Id == markerId);
                var outboxVisible = await context.Set<MessageBridgeOutboxMessage>()
                    .AnyAsync(x => x.Id == outboxId);
                return !markerVisible && !outboxVisible;
            },
            ObservationWindow,
            "Uncommitted business and outbox rows became externally visible.");

    public Task WaitForRowsAsync(Guid markerId, string outboxId) =>
        IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () =>
            {
                await using var context = CreateDbContext();
                var markerVisible = await context.BusinessMarkers.AnyAsync(x => x.Id == markerId);
                var outboxVisible = await context.Set<MessageBridgeOutboxMessage>()
                    .AnyAsync(x => x.Id == outboxId);
                return markerVisible && outboxVisible ? new RowsVisible() : null;
            },
            "Committed business and outbox rows did not become visible.");

    public async Task<MessageBridgeOutboxMessage> GetOutboxAsync(string outboxId)
    {
        await using var context = CreateDbContext();
        return await context.Set<MessageBridgeOutboxMessage>()
            .AsNoTracking()
            .SingleAsync(message => message.Id == outboxId);
    }

    public Task<MessageBridgeOutboxMessage> WaitForPublishedAsync(string outboxId) =>
        IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () =>
            {
                var message = await GetOutboxAsync(outboxId);
                return message.PublishedAtUtc is not null ? message : null;
            },
            $"Outbox row '{outboxId}' was not marked published.");

    public Task WaitForAttemptAsync(int expectedAttempts) =>
        IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () =>
            {
                if (_dispatcher.ExecuteTask is { IsCompleted: true } completed)
                {
                    await completed;
                }

                return Transport.Attempts >= expectedAttempts ? new AttemptObserved() : null;
            },
            $"Transport did not reach attempt {expectedAttempts}.");

    public async Task AssertNoDuplicateDispatchAsync(string messageId, int expectedAttempts)
    {
        await IntegrationEnvironmentFixture.AssertRemainsAsync(
            () => Task.FromResult(Transport.Attempts == expectedAttempts),
            ObservationWindow,
            $"Transport attempted '{messageId}' more than {expectedAttempts} times.");
        await Probe.AssertCountRemainsAsync(messageId, expectedCount: 1);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_dispatcherStarted)
            {
                using var cancellation = new CancellationTokenSource(
                    IntegrationEnvironmentFixture.AssertionTimeout);
                await _dispatcher.StopAsync(cancellation.Token);
            }
        }
        finally
        {
            await DisposeInfrastructureAsync();
        }
    }

    private static async Task<OutboxDispatchScenario> StartInfrastructureAsync(
        IntegrationEnvironmentFixture fixture,
        string databaseName,
        OutboxTestDbContextFactory contextFactory,
        bool failFirst)
    {
        var rabbitMqConnectionString = fixture.GetRabbitMqConnectionString();
        var provider = BuildTransportProvider(rabbitMqConnectionString);
        var bus = provider.GetRequiredService<IBusControl>();
        try
        {
            using var cancellation = new CancellationTokenSource(
                IntegrationEnvironmentFixture.AssertionTimeout);
            await bus.StartAsync(cancellation.Token);
            var probe = await RabbitMqEnvelopeProbe.CreateAsync(
                rabbitMqConnectionString,
                GetEnvelopeExchangeName(bus));
            var realTransport = provider.GetRequiredService<IMessageBridgePublisherTransport>();
            var transport = new GatedPublishTransport(realTransport, failFirst);
            return new OutboxDispatchScenario(
                fixture,
                databaseName,
                provider,
                bus,
                contextFactory,
                probe,
                transport);
        }
        catch
        {
            await TryStopBusAsync(bus);
            await provider.DisposeAsync();
            throw;
        }
    }

    private static async Task EnsureSchemaCreatedAsync(OutboxTestDbContextFactory contextFactory)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
    }

    private async Task DisposeInfrastructureAsync()
    {
        try
        {
            await Probe.DisposeAsync();
        }
        finally
        {
            try
            {
                await TryStopBusAsync(_bus);
                await _provider.DisposeAsync();
            }
            finally
            {
                await _fixture.DropDatabaseAsync(_databaseName);
            }
        }
    }

    private static ServiceProvider BuildTransportProvider(string rabbitMqConnectionString)
    {
        var services = new ServiceCollection();
        services.AddMassTransit(registration =>
            registration.UsingRabbitMq((_, configurator) =>
                configurator.Host(new Uri(rabbitMqConnectionString))));
        services.AddMessageBridgePublisher(options =>
        {
            options.DefaultTenantId = "integration";
            options.AllowedTenantIds = ["integration"];
        });
        return services.BuildServiceProvider();
    }

    private static string GetEnvelopeExchangeName(IBus bus)
    {
        var envelopeType = typeof(IMessageBridgePublisherTransport).Assembly.GetType(
            "MessageBridge.Publisher.Internal." +
            "MassTransitMessageBridgeTransport+MessageBridgePayloadEnvelope",
            throwOnError: true)!;
        if (!bus.Topology.TryGetPublishAddress(envelopeType, out var publishAddress))
        {
            throw new InvalidOperationException("MassTransit did not provide the outbox envelope address.");
        }

        return Uri.UnescapeDataString(publishAddress.Segments[^1].TrimEnd('/'));
    }

    private static MessageBridgeOutboxDispatcherHostedService<OutboxTestDbContext> CreateDispatcher(
        IDbContextFactory<OutboxTestDbContext> contextFactory,
        IMessageBridgePublisherTransport transport) =>
        new(
            contextFactory,
            transport,
            Options.Create(new MessageBridgeOutboxOptions
            {
                BatchSize = 1,
                Concurrency = 1,
                PollIntervalMilliseconds = 50,
                MaxRetryAttempts = 1,
                RetryDelayMilliseconds = 100,
                RetryBackoffMultiplier = 1,
            }));

    private static async Task TryStopBusAsync(IBusControl bus)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(
                IntegrationEnvironmentFixture.AssertionTimeout);
            await bus.StopAsync(cancellation.Token);
        }
        catch
        {
            // Preserve the primary failure while still attempting later cleanup.
        }
    }

    private sealed class RowsVisible;
    private sealed class AttemptObserved;
}

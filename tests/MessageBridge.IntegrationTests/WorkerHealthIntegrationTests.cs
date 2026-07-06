using FluentAssertions;
using MassTransit;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

public sealed class WorkerHealthIntegrationTests : IAsyncLifetime
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
    public async Task WorkerStartup_BusResolvesSuccessfully()
    {
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();

        // IBus resolved without exception
        bus.Should().NotBeNull();
    }


    [Fact]
    public async Task WorkerDependencies_DatabaseConnected()
    {
        var dbContext = _scope.ServiceProvider.GetRequiredService<MessageBridgeDbContext>();
        var canConnect = await dbContext.Database.CanConnectAsync();

        canConnect.Should().BeTrue();
    }

    [Fact]
    public async Task WorkerReadiness_AllServicesHealthy()
    {
        var bus = _scope.ServiceProvider.GetRequiredService<IBus>();
        var dbContext = _scope.ServiceProvider.GetRequiredService<MessageBridgeDbContext>();

        bus.Should().NotBeNull();
        var dbHealthy = await dbContext.Database.CanConnectAsync();

        dbHealthy.Should().BeTrue();
    }
}

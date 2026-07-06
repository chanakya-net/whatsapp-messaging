using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.RabbitMq;
using Xunit;

namespace MessageBridge.IntegrationTests.Fixtures;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _container = new RabbitMqBuilder()
            .WithImage("rabbitmq:3.13-alpine")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public IServiceCollection RegisterServices(
        IServiceCollection services,
        MessageBridgeDbContext dbContext)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = _connectionString,
                ["MessageBridgeTopology:ExchangeName"] = "messagebridge.commands",
                ["MessageBridgeTopology:WhatsAppRoutingKey"] = "messagebridge.whatsapp.send",
                ["MessageBridgeTopology:EmailRoutingKey"] = "messagebridge.email.confirm",
            })
            .Build();

        services.AddSingleton(dbContext);
        services.AddScoped<IMessageProcessingStore>(sp => new MessageProcessingStore(sp.GetRequiredService<MessageBridgeDbContext>()));
        services.AddMessageBridgeMassTransit(config);

        return services;
    }
}

using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
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
        MessageBridgeDbContext dbContext,
        Action<IBusRegistrationConfigurator>? configureMassTransit = null)
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

        var connectionString = dbContext.Database.GetConnectionString()
            ?? dbContext.Database.GetDbConnection().ConnectionString;
        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.SectionName));
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            return new MessageBridgeDbContext(options);
        });

        services.AddScoped<IMessageProcessingStore>(sp => new MessageProcessingStore(sp.GetRequiredService<MessageBridgeDbContext>()));

        if (configureMassTransit is null)
        {
            services.AddMessageBridgeMassTransit(config);
            return services;
        }

        services.AddMassTransit(bus =>
        {
            configureMassTransit(bus);

            bus.UsingRabbitMq((ctx, cfg) =>
            {
                var rabbitOpts = ctx.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

                if (rabbitOpts.UsesConnectionString)
                {
                    cfg.Host(rabbitOpts.ConnectionString);
                }
                else
                {
                    cfg.Host(rabbitOpts.Host, rabbitOpts.Port, rabbitOpts.VirtualHost, h =>
                    {
                        h.Username(rabbitOpts.Username!);
                        h.Password(rabbitOpts.Password!);
                        if (rabbitOpts.UseSsl)
                            h.UseSsl(s => s.Protocol = System.Security.Authentication.SslProtocols.Tls12);
                    });
                }

                cfg.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}

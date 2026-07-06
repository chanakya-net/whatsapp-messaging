using MassTransit;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Messaging;

public static class MassTransitRegistration
{
    public static IServiceCollection AddMessageBridgeMassTransit(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<MessageBridgeTopologyOptions>(
            configuration.GetSection(MessageBridgeTopologyOptions.SectionName));

        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqValidateOptions>();

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<SendWhatsAppMessageConsumer>();
            bus.AddConsumer<SendEmailConfirmationConsumer>();

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

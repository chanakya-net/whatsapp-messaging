using MassTransit;
using FluentValidation;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Options;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersistenceStore = MessageBridge.Application.Persistence.IMessageProcessingStore;
using LegacyStore = MessageBridge.Application.Abstractions.IMessageProcessingStore;

namespace MessageBridge.Infrastructure.Messaging;

public static class MassTransitRegistration
{
    public static IServiceCollection AddMessageBridgeMassTransit(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var topologyOptions = configuration.GetSection(MessageBridgeTopologyOptions.SectionName)
            .Get<MessageBridgeTopologyOptions>() ?? new MessageBridgeTopologyOptions();

        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<MessageBridgeTopologyOptions>(
            configuration.GetSection(MessageBridgeTopologyOptions.SectionName));
        services.Configure<TransportRetryOptions>(
            configuration.GetSection(TransportRetryOptions.SectionName));

        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqValidateOptions>();
        services.AddDbContext<MessageBridgeDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
        services.AddScoped<PersistenceStore, MessageProcessingStore>();
        services.AddScoped<LegacyStore, LegacyMessageProcessingStoreAdapter>();
        services.AddScoped<MessageProcessingCoordinator>();
        services.AddSingleton<IValidator<SendWhatsAppMessage>, SendWhatsAppMessageValidator>();
        services.AddSingleton<IValidator<SendEmailConfirmation>, SendEmailConfirmationValidator>();

        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(
                new KebabCaseEndpointNameFormatter(
                    topologyOptions.EnvironmentPrefix,
                    includeNamespace: false));
            bus.AddDelayedMessageScheduler();
            bus.AddConsumer<SendWhatsAppMessageConsumer>();
            bus.AddConsumer<SendEmailConfirmationConsumer>();
            bus.AddConsumer<SendWhatsAppMessageFaultConsumer>();
            bus.AddConsumer<SendEmailConfirmationFaultConsumer>();
            bus.AddRabbitMqConfigureEndpointsCallback((ctx, _, cfg) =>
            {
                var retryOptions = ctx.GetRequiredService<IOptions<TransportRetryOptions>>().Value;
                var redeliveryIntervals = retryOptions.DelayedRedeliveryIntervals.Length > 0
                    ? retryOptions.DelayedRedeliveryIntervals
                    : TransportRetryOptions.DefaultDelayedRedeliveryIntervals;

                cfg.UseDelayedRedelivery(redelivery => redelivery.Intervals(redeliveryIntervals));
                cfg.UseMessageRetry(retry => retry.Immediate(retryOptions.ImmediateRetryCount));
            });

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

                cfg.UseDelayedMessageScheduler();
                cfg.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}

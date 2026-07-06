using MessageBridge.Infrastructure.Persistence;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMessageBridgeMassTransit(configuration);
        services.AddDbContextFactory<MessageBridgeDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
        services.AddScoped<IMessageProcessingStore, MessageProcessingStore>();

        services.AddOptions<MessageProcessingHistoryOptions>()
            .Bind(configuration.GetSection(MessageProcessingHistoryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHostedService<StaleProcessingRecoveryService>();
        services.AddHostedService<ProcessingHistoryCleanupService>();

        return services;
    }
}

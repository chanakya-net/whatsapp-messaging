using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;

namespace MessageBridge.Publisher.EntityFrameworkCore;

public static class DependencyInjection
{
    public static IServiceCollection AddMessageBridgeOutboxPublisher<TContext>(
        this IServiceCollection services,
        Action<MessageBridgeOutboxOptions> configure)
        where TContext : DbContext
    {
        AddCoreServices(services, configure);

        services.AddHostedService<MessageBridgeOutboxDispatcherHostedService<TContext>>();
        services.AddHostedService<MessageBridgeOutboxCleanupHostedService<TContext>>();

        return services;
    }

    public static IServiceCollection AddMessageBridgeOutboxDispatcher<TContext>(
        this IServiceCollection services,
        Action<MessageBridgeOutboxOptions> configure)
        where TContext : DbContext
    {
        AddCoreServices(services, configure);
        services.AddHostedService<MessageBridgeOutboxDispatcherHostedService<TContext>>();
        return services;
    }

    public static IServiceCollection AddMessageBridgeOutboxCleanup<TContext>(
        this IServiceCollection services,
        Action<MessageBridgeOutboxOptions> configure)
        where TContext : DbContext
    {
        AddCoreServices(services, configure);
        services.AddHostedService<MessageBridgeOutboxCleanupHostedService<TContext>>();
        return services;
    }

    private static void AddCoreServices(
        IServiceCollection services,
        Action<MessageBridgeOutboxOptions> configure)
    {
        services.AddScoped<IMessageBridgeOutboxWriter, MessageBridgeOutboxWriter>();
        services.AddOptions<MessageBridgeOutboxOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<MessageBridgeOutboxOptions>, MessageBridgeOutboxOptionsValidator>();
    }
}

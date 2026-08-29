using ErrorOr;
using MessageBridge.Application.Persistence;
using LegacyMessageProcessingStore = MessageBridge.Application.Abstractions.IMessageProcessingStore;
using ITenantConfigurationProvider = MessageBridge.Application.Abstractions.ITenantConfigurationProvider;
using IProviderRateLimiter = MessageBridge.Application.Abstractions.IProviderRateLimiter;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.Infrastructure.Providers;
using MessageBridge.Infrastructure.RateLimiting;
using MessageBridge.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MessageBridge.Application.Providers;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MessageBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMessageBridgeMassTransit(configuration);
        services.AddMessageBridgeProcessingStore(configuration);
        services.AddMessageBridgeProviders(configuration);
        services.AddMessageBridgeTenancy(configuration);
        services.AddMessageBridgeRateLimiting(configuration);
        return services;
    }

    private static IServiceCollection AddMessageBridgeTenancy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TenantOptions>()
            .Bind(configuration.GetSection(TenantOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TenantOptions>, TenantOptionsValidator>();
        services.AddSingleton<ITenantConfigurationProvider, ConfigurationTenantConfigurationProvider>();

        return services;
    }

    private static IServiceCollection AddMessageBridgeProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ProviderOptions>()
            .Bind(configuration.GetSection(ProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProviderOptions>, ProviderOptionsValidator>();
        services.AddSingleton<IWhatsAppMessageSender, PlaceholderWhatsAppMessageSender>();
        services.AddSingleton<IEmailConfirmationSender, PlaceholderEmailConfirmationSender>();

        return services;
    }

    private static IServiceCollection AddMessageBridgeRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ProviderRateLimitOptions>()
            .Bind(configuration.GetSection(ProviderRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProviderRateLimitOptions>, ProviderRateLimitOptionsValidator>();
        services.AddSingleton<IProviderRateLimiter, MessageBridge.Infrastructure.RateLimiting.InMemoryProviderRateLimiter>();

        return services;
    }

    public static IServiceCollection AddMessageBridgeProcessingStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddSingleton(sp => NpgsqlDataSourceFactory.Create(
            sp.GetRequiredService<IOptions<DatabaseOptions>>().Value));
        services.AddDbContextFactory<MessageBridgeDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IMessageProcessingStore, MessageProcessingStore>();
        services.AddScoped<MessageProcessingCoordinator>();
        services.AddSingleton<LegacyMessageProcessingStore, TrackingMessageProcessingStoreAdapter>();
        services.AddOptions<MessageProcessingHistoryOptions>()
            .Bind(configuration.GetSection(MessageProcessingHistoryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<StaleProcessingRecoveryService>();
        services.AddHostedService<ProcessingHistoryCleanupService>();
        return services;
    }

    private sealed class TrackingMessageProcessingStoreAdapter : LegacyMessageProcessingStore
    {
        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }

}

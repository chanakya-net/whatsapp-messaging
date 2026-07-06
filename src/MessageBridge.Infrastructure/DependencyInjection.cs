using ErrorOr;
using MessageBridge.Application.Persistence;
using LegacyMessageProcessingStore = MessageBridge.Application.Abstractions.IMessageProcessingStore;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MessageBridge.Application.Providers;
using Microsoft.Extensions.Options;

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

    public static IServiceCollection AddMessageBridgeProcessingStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["MESSAGEBRIDGE_CONNECTION_STRING"]
            ?? "Host=localhost;Port=5432;Database=messagebridge;Username=postgres;Password=postgres";

        services.AddDbContext<MessageBridgeDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddSingleton<IDbContextFactory<MessageBridgeDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new RuntimeMessageBridgeDbContextFactory(options);
        });
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

    private sealed class RuntimeMessageBridgeDbContextFactory(
        DbContextOptions<MessageBridgeDbContext> options)
        : IDbContextFactory<MessageBridgeDbContext>
    {
        public MessageBridgeDbContext CreateDbContext() => new(options);

        public ValueTask<MessageBridgeDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CreateDbContext());
    }
}

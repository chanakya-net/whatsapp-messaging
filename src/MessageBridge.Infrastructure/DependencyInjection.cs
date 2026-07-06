using ErrorOr;
using MessageBridge.Application.Persistence;
using LegacyMessageProcessingStore = MessageBridge.Application.Abstractions.IMessageProcessingStore;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMessageBridgeMassTransit(configuration);
        services.AddMessageBridgeProcessingStore(configuration);
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
        services.AddScoped<IMessageProcessingStore, MessageProcessingStore>();
        services.AddScoped<MessageProcessingCoordinator>();
        services.AddSingleton<LegacyMessageProcessingStore, TrackingMessageProcessingStoreAdapter>();
        return services;
    }

    private sealed class TrackingMessageProcessingStoreAdapter : LegacyMessageProcessingStore
    {
        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }
}

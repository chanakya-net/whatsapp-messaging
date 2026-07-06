using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBridge.Worker;

internal static class WorkerRuntimeServices
{
    public static IServiceCollection AddWorkerRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<IWhatsAppMessageSender, NoopWhatsAppMessageSender>();
        services.AddSingleton<IEmailConfirmationSender, NoopEmailConfirmationSender>();
        services.AddSingleton<IMessageProcessingStore, NoopMessageProcessingStore>();
        services.AddSingleton<ITenantConfigurationProvider, NoopTenantConfigurationProvider>();
        services.AddSingleton<IProviderRateLimiter, NoopProviderRateLimiter>();

        return services;
    }

    private sealed class NoopWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class NoopEmailConfirmationSender : IEmailConfirmationSender
    {
        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class NoopMessageProcessingStore : IMessageProcessingStore
    {
        public Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }

    private sealed class NoopTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId)
            => Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, IsActive: true));
    }

    private sealed class NoopProviderRateLimiter : IProviderRateLimiter
    {
        public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType)
            => Task.FromResult<ErrorOr<Success>>(new Success());
    }
}

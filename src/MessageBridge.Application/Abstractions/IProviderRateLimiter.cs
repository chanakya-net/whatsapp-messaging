using ErrorOr;

namespace MessageBridge.Application.Abstractions;

public interface IProviderRateLimiter
{
    Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType);
}

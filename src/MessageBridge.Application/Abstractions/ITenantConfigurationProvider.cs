using ErrorOr;

namespace MessageBridge.Application.Abstractions;

public record TenantConfiguration(string TenantId, bool IsActive);

public interface ITenantConfigurationProvider
{
    Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId);
}

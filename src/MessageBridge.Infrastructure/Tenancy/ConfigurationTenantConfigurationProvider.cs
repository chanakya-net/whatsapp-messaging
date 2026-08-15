using ErrorOr;
using MessageBridge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Tenancy;

public sealed class ConfigurationTenantConfigurationProvider : ITenantConfigurationProvider
{
    private readonly IReadOnlySet<string> _allowedTenantIds;

    public ConfigurationTenantConfigurationProvider(IOptions<TenantOptions> options)
    {
        _allowedTenantIds = NormalizeTenantIds(options.Value.AllowedTenantIds);
    }

    public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId)
    {
        if (_allowedTenantIds.Contains(tenantId))
            return Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, IsActive: true));

        return Task.FromResult<ErrorOr<TenantConfiguration>>(
            Error.NotFound("Tenant.NotFound", $"Tenant '{tenantId}' is not in the configured allowlist."));
    }

    private static IReadOnlySet<string> NormalizeTenantIds(string? allowedTenantIds)
    {
        if (string.IsNullOrWhiteSpace(allowedTenantIds))
            return new HashSet<string>(StringComparer.Ordinal);

        return allowedTenantIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}

using ErrorOr;
using MessageBridge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.RateLimiting;

public sealed class InMemoryProviderRateLimiter : IProviderRateLimiter
{
    private sealed record WindowState(int PermitsRemaining, DateTime WindowOpenUtc);

    private readonly IOptions<ProviderRateLimitOptions> _options;
    private readonly object _lockObj = new();
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);

    public InMemoryProviderRateLimiter(IOptions<ProviderRateLimitOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return Task.FromResult<ErrorOr<Success>>(Error.Validation("TenantId.Required", "Tenant ID is required."));

        if (string.IsNullOrWhiteSpace(providerType))
            return Task.FromResult<ErrorOr<Success>>(Error.Validation("ProviderType.Required", "Provider type is required."));

        var key = BuildKey(tenantId, providerType);
        var opts = _options.Value;
        var now = DateTime.UtcNow;

        lock (_lockObj)
        {
            if (_windows.TryGetValue(key, out var state))
            {
                var windowElapsed = now - state.WindowOpenUtc;
                if (windowElapsed.TotalSeconds >= opts.WindowSizeSeconds)
                {
                    _windows[key] = new WindowState(opts.PermitsPerWindow - 1, now);
                    return Task.FromResult<ErrorOr<Success>>(new Success());
                }

                if (state.PermitsRemaining > 0)
                {
                    _windows[key] = state with { PermitsRemaining = state.PermitsRemaining - 1 };
                    return Task.FromResult<ErrorOr<Success>>(new Success());
                }

                return Task.FromResult<ErrorOr<Success>>(Error.Conflict("RateLimit.Exceeded", "Rate limit exceeded."));
            }

            _windows[key] = new WindowState(opts.PermitsPerWindow - 1, now);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private static string BuildKey(string tenantId, string providerType)
    {
        var normalizedTenantId = tenantId.ToLowerInvariant();
        var normalizedProviderType = providerType.ToLowerInvariant();
        return $"{normalizedTenantId}|{normalizedProviderType}";
    }
}

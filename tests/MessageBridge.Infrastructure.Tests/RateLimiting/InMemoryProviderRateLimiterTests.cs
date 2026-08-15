using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Infrastructure.RateLimiting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.RateLimiting;

[Trait("Category", "Unit")]
public sealed class InMemoryProviderRateLimiterTests
{
    [Fact]
    public async Task CheckRateLimitAsync_returns_success_on_first_permit()
    {
        var options = Options.Create(new ProviderRateLimitOptions());
        var limiter = new InMemoryProviderRateLimiter(options);

        var result = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<Success>();
    }

    [Fact]
    public async Task CheckRateLimitAsync_tracks_permits_by_tenant_and_channel()
    {
        var options = Options.Create(new ProviderRateLimitOptions { WhatsAppPermitsPerWindow = 2, EmailPermitsPerWindow = 2 });
        var limiter = new InMemoryProviderRateLimiter(options);

        var r1 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r1.IsError.ShouldBeFalse();

        var r2 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r2.IsError.ShouldBeFalse();

        var r3 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r3.IsError.ShouldBeTrue();

        // Different tenant should get its own permits
        var r4 = await limiter.CheckRateLimitAsync("tenant-2", "whatsapp");
        r4.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckRateLimitAsync_isolates_channels()
    {
        var options = Options.Create(new ProviderRateLimitOptions { WhatsAppPermitsPerWindow = 1, EmailPermitsPerWindow = 1 });
        var limiter = new InMemoryProviderRateLimiter(options);

        var r1 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r1.IsError.ShouldBeFalse();

        var r2 = await limiter.CheckRateLimitAsync("tenant-1", "email");
        r2.IsError.ShouldBeFalse();

        var r3 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r3.IsError.ShouldBeTrue();

        var r4 = await limiter.CheckRateLimitAsync("tenant-1", "email");
        r4.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckRateLimitAsync_normalizes_tenant_id_to_lowercase()
    {
        var options = Options.Create(new ProviderRateLimitOptions { WhatsAppPermitsPerWindow = 1, EmailPermitsPerWindow = 1 });
        var limiter = new InMemoryProviderRateLimiter(options);

        var r1 = await limiter.CheckRateLimitAsync("Tenant-1", "whatsapp");
        r1.IsError.ShouldBeFalse();

        var r2 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r2.IsError.ShouldBeTrue();

        var r3 = await limiter.CheckRateLimitAsync("TENANT-1", "whatsapp");
        r3.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckRateLimitAsync_exhaustion_returns_transient_error()
    {
        var options = Options.Create(new ProviderRateLimitOptions { WhatsAppPermitsPerWindow = 1, EmailPermitsPerWindow = 1 });
        var limiter = new InMemoryProviderRateLimiter(options);

        await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        var result = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");

        result.IsError.ShouldBeTrue();
        var error = result.Errors[0];
        error.Type.ShouldBe(ErrorType.Conflict);
        error.Code.ShouldBe("RateLimit.Exceeded");
    }

    [Fact]
    public async Task CheckRateLimitAsync_window_resets_after_configured_duration()
    {
        var options = Options.Create(new ProviderRateLimitOptions { WhatsAppPermitsPerWindow = 1, EmailPermitsPerWindow = 1, WindowSizeSeconds = 1 });
        var limiter = new InMemoryProviderRateLimiter(options);

        var r1 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r1.IsError.ShouldBeFalse();

        var r2 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r2.IsError.ShouldBeTrue();

        await Task.Delay(1100);

        var r3 = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        r3.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckRateLimitAsync_rejects_unsupported_provider_type()
    {
        var options = Options.Create(new ProviderRateLimitOptions());
        var limiter = new InMemoryProviderRateLimiter(options);

        var result = await limiter.CheckRateLimitAsync("tenant-1", "sms");

        result.IsError.ShouldBeTrue();
        result.Errors[0].Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public async Task CheckRateLimitAsync_default_permits_exactly_60_requests()
    {
        var options = Options.Create(new ProviderRateLimitOptions());
        var limiter = new InMemoryProviderRateLimiter(options);

        for (int i = 0; i < 60; i++)
        {
            var result = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
            result.IsError.ShouldBeFalse($"Request {i + 1} should succeed");
        }

        var finalResult = await limiter.CheckRateLimitAsync("tenant-1", "whatsapp");
        finalResult.IsError.ShouldBeTrue();
        finalResult.Errors[0].Type.ShouldBe(ErrorType.Conflict);
        finalResult.Errors[0].Code.ShouldBe("RateLimit.Exceeded");
    }
}

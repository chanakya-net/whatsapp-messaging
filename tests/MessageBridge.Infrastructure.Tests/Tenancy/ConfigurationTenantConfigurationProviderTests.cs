using MessageBridge.Infrastructure.Tenancy;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Tenancy;

[Trait("Category", "Unit")]
public sealed class ConfigurationTenantConfigurationProviderTests
{
    [Fact]
    public async Task Configured_tenant_is_allowed()
    {
        var provider = CreateProvider("tenant-a,tenant-b");

        var result = await provider.GetTenantConfigAsync("tenant-a");

        result.IsError.ShouldBeFalse();
        result.Value.TenantId.ShouldBe("tenant-a");
        result.Value.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Entries_are_trimmed_before_comparison()
    {
        var provider = CreateProvider("  tenant-a  ,  tenant-b  ");

        var result = await provider.GetTenantConfigAsync("tenant-a");

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Duplicate_entries_are_deduplicated_without_error()
    {
        var provider = CreateProvider("tenant-a,tenant-a,tenant-a");

        var result = await provider.GetTenantConfigAsync("tenant-a");

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Comparison_is_ordinal_case_sensitive()
    {
        var provider = CreateProvider("tenant-a");

        var result = await provider.GetTenantConfigAsync("TENANT-A");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Empty_allowlist_rejects_every_tenant()
    {
        var provider = CreateProvider(string.Empty);

        var result = await provider.GetTenantConfigAsync("tenant-a");

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    private static ConfigurationTenantConfigurationProvider CreateProvider(string allowedTenantIds) =>
        new(Options.Create(new TenantOptions { AllowedTenantIds = allowedTenantIds }));
}

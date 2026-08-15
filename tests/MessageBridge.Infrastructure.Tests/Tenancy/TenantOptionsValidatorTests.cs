using MessageBridge.Infrastructure.Tenancy;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Tenancy;

[Trait("Category", "Unit")]
public sealed class TenantOptionsValidatorTests
{
    [Fact]
    public void Empty_allowlist_is_valid()
    {
        var validation = new TenantOptionsValidator().Validate(null, new TenantOptions());

        validation.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Configured_allowlist_is_valid()
    {
        var options = new TenantOptions { AllowedTenantIds = "tenant-a, tenant-b" };

        var validation = new TenantOptionsValidator().Validate(null, options);

        validation.Succeeded.ShouldBeTrue();
    }
}

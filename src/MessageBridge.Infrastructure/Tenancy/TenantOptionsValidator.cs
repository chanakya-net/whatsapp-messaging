using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Tenancy;

public sealed class TenantOptionsValidator : IValidateOptions<TenantOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantOptions options) =>
        options.AllowedTenantIds is null
            ? ValidateOptionsResult.Fail($"{nameof(TenantOptions.AllowedTenantIds)} must not be null.")
            : ValidateOptionsResult.Success;
}

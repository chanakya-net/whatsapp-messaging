namespace MessageBridge.Infrastructure.Tenancy;

public sealed class TenantOptions
{
    public const string SectionName = "MessageBridge:Tenants";

    public string AllowedTenantIds { get; set; } = string.Empty;
}

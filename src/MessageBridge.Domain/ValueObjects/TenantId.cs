using System.Text.RegularExpressions;
using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record TenantId
{
    private const int MaxLength = 100;
    private const int MinLength = 2;
    private static readonly Regex FormatRegex = new("^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    private TenantId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<TenantId> Create(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return Error.Validation(
                "TenantId.InvalidFormat",
                "Tenant id is required.");

        var normalized = tenantId.Trim();
        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "TenantId.InvalidLength",
                $"Tenant id must be between {MinLength} and {MaxLength} characters.");

        if (!FormatRegex.IsMatch(normalized))
            return Error.Validation(
                "TenantId.InvalidFormat",
                "Tenant id can only contain letters, digits, underscores, and hyphens.");

        return new TenantId(normalized);
    }

    public override string ToString() => Value;
}

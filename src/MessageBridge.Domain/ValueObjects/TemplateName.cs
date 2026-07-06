using ErrorOr;
using System.Text.RegularExpressions;

namespace MessageBridge.Domain.ValueObjects;

public sealed record TemplateName
{
    private const int MaxLength = 100;
    private const int MinLength = 3;
    private static readonly Regex FormatRegex = new(
        "^[a-z0-9][a-z0-9_\\-]{2,}$",
        RegexOptions.Compiled);

    private TemplateName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<TemplateName> Create(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return Error.Validation(
                "TemplateName.InvalidFormat",
                "Template name is required.");

        var normalized = templateName.Trim();
        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "TemplateName.InvalidLength",
                $"Template name must be between {MinLength} and {MaxLength} characters.");

        if (!FormatRegex.IsMatch(normalized))
            return Error.Validation(
                "TemplateName.InvalidFormat",
                "Template name can only contain lowercase letters, numbers, underscores, and hyphens.");

        return new TemplateName(normalized);
    }

    public override string ToString() => Value;
}

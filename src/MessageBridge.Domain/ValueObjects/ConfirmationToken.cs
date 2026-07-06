using ErrorOr;
using System.Text.RegularExpressions;

namespace MessageBridge.Domain.ValueObjects;

public sealed record ConfirmationToken
{
    private const int MaxLength = 256;
    private const int MinLength = 16;
    private static readonly Regex FormatRegex = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private ConfirmationToken(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<ConfirmationToken> Create(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Error.Validation(
                "ConfirmationToken.InvalidFormat",
                "Confirmation token is required.");

        var normalized = token.Trim();
        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "ConfirmationToken.InvalidLength",
                $"Confirmation token must be between {MinLength} and {MaxLength} characters.");

        if (!FormatRegex.IsMatch(normalized))
            return Error.Validation(
                "ConfirmationToken.InvalidFormat",
                "Confirmation token must be URL-safe alphanumeric characters.");

        return new ConfirmationToken(normalized);
    }

    public override string ToString() => Value;
}

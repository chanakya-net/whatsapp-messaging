using System.Text.RegularExpressions;
using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record EmailAddress
{
    private const int MaxLength = 320;
    private const int MinLength = 3;
    private static readonly Regex FormatRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private EmailAddress(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<EmailAddress> Create(string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return Error.Validation(
                "EmailAddress.InvalidFormat",
                "Email address is required.");

        var normalized = emailAddress.Trim();
        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "EmailAddress.InvalidLength",
                $"Email address must be between {MinLength} and {MaxLength} characters.");

        if (!FormatRegex.IsMatch(normalized))
            return Error.Validation(
                "EmailAddress.InvalidFormat",
                "Email address must be a valid email format.");

        return new EmailAddress(normalized);
    }

    public override string ToString() => Value;
}

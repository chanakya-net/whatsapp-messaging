using System.Text.RegularExpressions;
using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record MessageId
{
    private const int MaxLength = 200;
    private const int MinLength = 4;
    private static readonly Regex FormatRegex = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.Compiled);

    private MessageId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<MessageId> Create(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return Error.Validation(
                "MessageId.InvalidFormat",
                "Message id is required.");

        var normalized = messageId.Trim();
        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "MessageId.InvalidLength",
                $"Message id must be between {MinLength} and {MaxLength} characters.");

        if (!FormatRegex.IsMatch(normalized))
            return Error.Validation(
                "MessageId.InvalidFormat",
                "Message id can only contain letters, digits, underscores, dots, dashes, and colons.");

        return new MessageId(normalized);
    }

    public override string ToString() => Value;
}

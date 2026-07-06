using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record PhoneNumber
{
    private const int MaxLength = 25;
    private const int MinLength = 8;

    private PhoneNumber(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<PhoneNumber> Create(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return Error.Validation(
                "PhoneNumber.InvalidFormat",
                "Phone number is required.");

        var normalized = new string(phoneNumber.Where(char.IsDigit).ToArray());

        if (normalized.Length is < MinLength or > MaxLength)
            return Error.Validation(
                "PhoneNumber.InvalidLength",
                $"Phone number must be between {MinLength} and {MaxLength} digits.");

        if (normalized[0] == '0')
            return Error.Validation(
                "PhoneNumber.InvalidFormat",
                "Phone number cannot start with zero.");

        return new PhoneNumber(normalized);
    }

    public override string ToString() => Value;
}

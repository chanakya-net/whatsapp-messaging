using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record CorrelationId
{
    private const int MaxLength = 200;
    private CorrelationId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorOr<CorrelationId> Create(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return Error.Validation(
                "CorrelationId.InvalidFormat",
                "Correlation id is required.");

        var normalized = correlationId.Trim();
        if (normalized.Length > MaxLength)
            return Error.Validation(
                "CorrelationId.InvalidLength",
                $"Correlation id must be {MaxLength} characters or fewer.");

        if (!Guid.TryParse(normalized, out _))
            return Error.Validation(
                "CorrelationId.InvalidFormat",
                "Correlation id must be a valid GUID.");

        return new CorrelationId(normalized);
    }

    public override string ToString() => Value;
}

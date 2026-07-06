using ErrorOr;

namespace MessageBridge.Domain.ValueObjects;

public sealed record CorrelationId
{
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
        if (!Guid.TryParse(normalized, out _))
            return Error.Validation(
                "CorrelationId.InvalidFormat",
                "Correlation id must be a valid GUID.");

        return new CorrelationId(normalized);
    }

    public override string ToString() => Value;
}

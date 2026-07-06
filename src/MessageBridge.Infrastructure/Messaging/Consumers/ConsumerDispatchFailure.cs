using ErrorOr;
using MassTransit;
using MessageBridge.Domain.Privacy;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

internal static class ConsumerDispatchFailure
{
    public static bool IsRejected(IReadOnlyList<Error> errors)
    {
        return errors.Any(error =>
            error.Type == ErrorType.Validation
            || error.Code.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || error.Description.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    public static Exception CreateException(string contractName, IReadOnlyList<Error> errors)
    {
        var details = DescribeErrors(errors);

        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(details)
                ? $"Application handler failed for {contractName}."
                : $"Application handler failed for {contractName}: {details}");
    }

    public static string DescribeErrors(IReadOnlyList<Error> errors)
    {
        if (errors.Count == 0)
        {
            return string.Empty;
        }

        var details = string.Join(
            "; ",
            errors.Select(error =>
            {
                var description = ErrorSanitizer.Sanitize(error.Description);
                return string.IsNullOrWhiteSpace(description)
                    ? error.Code
                    : $"{error.Code}: {description}";
            }));

        return details;
    }

    public static string DescribeExceptions(IEnumerable<(string Type, string Message)> exceptions)
    {
        return string.Join(
            "; ",
            exceptions.Select(exception =>
            {
                var message = ErrorSanitizer.Sanitize(exception.Message);
                return string.IsNullOrWhiteSpace(message)
                    ? exception.Type
                    : $"{exception.Type}: {message}";
            }));
    }
}

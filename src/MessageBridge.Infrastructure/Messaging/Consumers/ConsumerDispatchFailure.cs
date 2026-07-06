using ErrorOr;
using MessageBridge.Domain.Privacy;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

internal static class ConsumerDispatchFailure
{
    public static void ThrowIfError(string contractName, ErrorOr<Success> result)
    {
        if (!result.IsError)
        {
            return;
        }

        var details = string.Join(
            "; ",
            result.Errors.Select(error =>
            {
                var description = ErrorSanitizer.Sanitize(error.Description);
                return string.IsNullOrWhiteSpace(description)
                    ? error.Code
                    : $"{error.Code}: {description}";
            }));

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(details)
                ? $"Application handler failed for {contractName}."
                : $"Application handler failed for {contractName}: {details}");
    }
}

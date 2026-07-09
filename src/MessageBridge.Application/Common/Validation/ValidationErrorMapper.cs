using ErrorOr;
using FluentValidation.Results;

namespace MessageBridge.Application.Common.Validation;

public static class ValidationErrorMapper
{
    public static ErrorOr<T> ToErrorOr<T>(this ValidationResult validationResult, T value)
    {
        if (validationResult.IsValid)
        {
            return value;
        }

        var errors = validationResult.Errors
            .Select(failure =>
                Error.Validation(
                    $"Validation.{failure.PropertyName}",
                    failure.ErrorMessage))
            .ToArray();

        return errors;
    }
}

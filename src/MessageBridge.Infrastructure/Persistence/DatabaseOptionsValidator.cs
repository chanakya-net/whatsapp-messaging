using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        var failures = new List<string>();

        Require(options.Host, nameof(options.Host), failures);
        Require(options.Database, nameof(options.Database), failures);
        Require(options.Username, nameof(options.Username), failures);

        if (options.Port is < 1 or > 65535)
            failures.Add("Port must be between 1 and 65535.");

        if (options.MaxPoolSize < 1)
            failures.Add("MaxPoolSize must be greater than zero.");

        if (options.UseEntraAuth && !string.IsNullOrWhiteSpace(options.Password))
            failures.Add("Password must not be stored when UseEntraAuth is enabled.");

        if (!options.UseEntraAuth && string.IsNullOrWhiteSpace(options.Password))
            failures.Add("Password is required when UseEntraAuth is disabled.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(string? value, string propertyName, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{propertyName} is required.");
    }
}

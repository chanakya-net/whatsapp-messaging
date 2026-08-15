using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace MessageBridge.Infrastructure.RateLimiting;

public sealed class ProviderRateLimitOptions
{
    public const string SectionName = "MessageBridge:RateLimiting";

    [Range(1, int.MaxValue)]
    public int WhatsAppPermitsPerWindow { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int EmailPermitsPerWindow { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int WindowSizeSeconds { get; set; } = 60;
}

public sealed class ProviderRateLimitOptionsValidator : IValidateOptions<ProviderRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderRateLimitOptions options)
    {
        var failures = new List<string>();

        if (options.WhatsAppPermitsPerWindow < 1)
            failures.Add($"{nameof(ProviderRateLimitOptions.WhatsAppPermitsPerWindow)} must be >= 1.");

        if (options.EmailPermitsPerWindow < 1)
            failures.Add($"{nameof(ProviderRateLimitOptions.EmailPermitsPerWindow)} must be >= 1.");

        if (options.WindowSizeSeconds < 1)
            failures.Add($"{nameof(ProviderRateLimitOptions.WindowSizeSeconds)} must be >= 1.");

        return failures.Count is 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

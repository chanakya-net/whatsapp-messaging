using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace MessageBridge.Worker.Observability;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public string ServiceName { get; set; } = "MessageBridge.Worker";

    public bool MetricsEndpointEnabled { get; set; }

    public string? OtlpEndpoint { get; set; }
}

public sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            failures.Add("ServiceName must be provided.");
        }

        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint) &&
            (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint) ||
             (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add("OtlpEndpoint must be a valid absolute http/https URL when provided.");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

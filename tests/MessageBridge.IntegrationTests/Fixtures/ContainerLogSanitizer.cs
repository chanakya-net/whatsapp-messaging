using System.Text.RegularExpressions;
using MessageBridge.Domain.Privacy;
using Npgsql;

namespace MessageBridge.IntegrationTests.Fixtures;

internal static class ContainerLogSanitizer
{
    private const string CredentialMarker = "[REDACTED_CREDENTIAL]";
    private const string UriUserInfoMarker = "${scheme}[REDACTED_URI_USERINFO]@";

    private static readonly Regex UriUserInfoRegex = new(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/\s@]+@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuotedUserRegex = new(
        """(?<prefix>\buser(?:name)?(?:\s+id)?\b\s*(?:[:=]\s*)?["'])(?<value>[^"'\r\n]+)(?<suffix>["'])""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string Sanitize(
        string? diagnostics,
        params string?[] connectionStrings)
    {
        var sanitized = UriUserInfoRegex.Replace(
            diagnostics ?? string.Empty,
            UriUserInfoMarker);
        sanitized = QuotedUserRegex.Replace(
            sanitized,
            match => $"{match.Groups["prefix"].Value}{CredentialMarker}{match.Groups["suffix"].Value}");

        foreach (var credential in GetCredentials(connectionStrings))
        {
            sanitized = RedactKnownCredential(sanitized, credential);
        }

        return ErrorSanitizer.Sanitize(sanitized);
    }

    private static string RedactKnownCredential(string diagnostics, string credential)
    {
        return Regex.Replace(
            diagnostics,
            Regex.Escape(credential),
            CredentialMarker,
            RegexOptions.IgnoreCase);
    }

    private static IEnumerable<string> GetCredentials(IEnumerable<string?> connectionStrings) =>
        connectionStrings
            .Where(connectionString => !string.IsNullOrWhiteSpace(connectionString))
            .SelectMany(GetCredentials)
            .Where(credential => !string.IsNullOrWhiteSpace(credential))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(credential => credential.Length);

    private static IEnumerable<string> GetCredentials(string? connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.UserInfo))
        {
            foreach (var credential in uri.UserInfo.Split(':', 2))
            {
                yield return Uri.UnescapeDataString(credential);
            }

            yield break;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(builder.Username))
        {
            yield return builder.Username;
        }

        if (!string.IsNullOrWhiteSpace(builder.Password))
        {
            yield return builder.Password;
        }
    }
}

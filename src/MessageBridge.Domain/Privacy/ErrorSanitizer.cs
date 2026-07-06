using System.Text.RegularExpressions;

namespace MessageBridge.Domain.Privacy;

public static class ErrorSanitizer
{
    private static readonly Regex EmailRegex = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PhoneRegex = new(
        @"(?:\+?\d[\d\s().-]{7,}\d)",
        RegexOptions.Compiled);

    private static readonly Regex SecretRegex = new(
        @"(?i)(?<prefix>[\s\""'`{=;:]|^)(?<key>password|passwd|pwd|token|secret|api[_-]?key|access[_-]?token|connection[_-]?string|authorization)(?<sep>\s*[:=]\s*)(?<value>[^\s;\""'`{,}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlainTokenRegex = new(
        @"(?i)\b[a-z0-9_-]{24,}\b",
        RegexOptions.Compiled);

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var sanitized = SecretRegex.Replace(
            message,
            match => $"{match.Groups["prefix"].Value}[REDACTED_{match.Groups["key"].Value.ToUpperInvariant()}]");

        sanitized = EmailRegex.Replace(sanitized, match => RecipientMasker.MaskEmailAddress(match.Value));
        sanitized = PhoneRegex.Replace(sanitized, match => RecipientMasker.MaskPhoneNumber(match.Value));
        sanitized = PlainTokenRegex.Replace(
            sanitized,
            match => $"<{match.Value.AsSpan(0, 3)}...redacted>");

        return sanitized;
    }
}

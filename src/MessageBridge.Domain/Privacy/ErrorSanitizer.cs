using System.Text;
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
        """(?i)(?<prefix>[\s"'`{=;:,]|^)(?<key>password|passwd|pwd|token|secret|api[_-]?key|access[_-]?token|connection[_-]?string|authorization|username|user\s*id|uid)(?:["'`])?(?<sep>\s*[:=]\s*)(?<value>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|`[^`]*`|[^\s;"'`{,}]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlainTokenRegex = new(
        @"(?i)\b[a-z0-9_-]{24,}\b",
        RegexOptions.Compiled);

    private static readonly Regex PayloadPrefixRegex = new(
        """(?i)\bpayload(?:["'`])?\s*[:=]\s*""",
        RegexOptions.Compiled);

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var sanitized = SecretRegex.Replace(message, RedactSecret);

        sanitized = RedactPayloadValues(sanitized);
        sanitized = EmailRegex.Replace(sanitized, match => RecipientMasker.MaskEmailAddress(match.Value));
        sanitized = PhoneRegex.Replace(sanitized, match => RecipientMasker.MaskPhoneNumber(match.Value));
        sanitized = PlainTokenRegex.Replace(
            sanitized,
            match => $"<{match.Value.AsSpan(0, 3)}...redacted>");

        return sanitized;
    }

    private static string RedactSecret(Match match)
    {
        var prefix = match.Groups["prefix"].Value;
        var marker = $"[REDACTED_{match.Groups["key"].Value.ToUpperInvariant()}]";
        return prefix is "\"" or "'" or "`"
            ? $"{prefix}{marker}{prefix}"
            : $"{prefix}{marker}";
    }

    private static string RedactPayloadValues(string message)
    {
        var result = new StringBuilder(message.Length);
        var position = 0;

        for (var match = PayloadPrefixRegex.Match(message); match.Success; match = PayloadPrefixRegex.Match(message, position))
        {
            result.Append(message, position, match.Index - position);
            result.Append(match.Value);
            result.Append("[REDACTED_PAYLOAD]");
            position = FindPayloadEnd(message, match.Index + match.Length);
        }

        result.Append(message, position, message.Length - position);
        return result.ToString();
    }

    private static int FindPayloadEnd(string message, int start)
    {
        if (start >= message.Length)
            return start;

        return message[start] switch
        {
            '{' or '[' => FindStructuredValueEnd(message, start),
            '\"' or '\'' => FindQuotedValueEnd(message, start),
            _ => FindTextValueEnd(message, start)
        };
    }

    private static int FindStructuredValueEnd(string message, int start)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;

        for (var index = start; index < message.Length; index++)
        {
            var current = message[index];
            if (quoted)
            {
                escaped = current == '\\' && !escaped;
                if (current == '\"' && !escaped)
                    quoted = false;
                else if (current != '\\')
                    escaped = false;
                continue;
            }

            if (current == '\"')
            {
                quoted = true;
                continue;
            }

            if (current is '{' or '[')
                depth++;
            else if (current is '}' or ']')
                depth--;

            if (depth == 0)
                return index + 1;
        }

        return message.Length;
    }

    private static int FindQuotedValueEnd(string message, int start)
    {
        var quote = message[start];

        for (var index = start + 1; index < message.Length; index++)
        {
            if (message[index] == quote && message[index - 1] != '\\')
                return index + 1;
        }

        return message.Length;
    }

    private static int FindTextValueEnd(string message, int start)
    {
        var delimiter = message.IndexOfAny([';', '\r', '\n'], start);
        return delimiter < 0 ? message.Length : delimiter;
    }
}

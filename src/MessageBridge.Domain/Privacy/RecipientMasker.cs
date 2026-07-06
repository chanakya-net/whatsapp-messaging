namespace MessageBridge.Domain.Privacy;

public static class RecipientMasker
{
    private const int PhoneVisibleSuffix = 4;

    public static string MaskPhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return string.Empty;

        var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (digits.Length <= PhoneVisibleSuffix)
            return new string('*', digits.Length);

        return new string('*', digits.Length - PhoneVisibleSuffix) + digits[^PhoneVisibleSuffix..];
    }

    public static string MaskEmailAddress(string? emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return string.Empty;

        var trimmed = emailAddress.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex <= 0 || atIndex >= trimmed.Length - 1)
            return "***";

        var local = trimmed[..atIndex];
        var domain = trimmed[(atIndex + 1)..];
        var dotIndex = domain.LastIndexOf('.');
        if (dotIndex < 0)
            return "***";

        var tld = domain[dotIndex..];
        if (local.Length <= 2)
            return $"{local[0]}***@***{tld}";

        var maskedLocal = $"{local[0]}***{local[^1]}";
        return $"{maskedLocal}@***{tld}";
    }
}

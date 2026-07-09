namespace MessageBridge.Domain.Tests.Privacy;

public class ErrorSanitizerTests
{
    [Theory]
    [InlineData("password=super_secret", "[REDACTED_PASSWORD]")]
    [InlineData("access_token=abc123", "[REDACTED_ACCESS_TOKEN]")]
    public void Sanitize_RedactsSecrets(string input, string expected)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldBe(expected);
    }

    [Fact]
    public void Sanitize_RedactsConnectionStrings()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(
            "connection_string=localhost");

        sanitized.ShouldNotContain("localhost");
        sanitized.ShouldContain("RED");
    }

    [Fact]
    public void Sanitize_RedactsEmails()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize("user=person@example.com");

        sanitized.ShouldBe("user=p***n@***.com");
    }

    [Fact]
    public void Sanitize_RedactsPhoneNumbers()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize("Contact +1 (415) 555-2671 immediately.");

        sanitized.ShouldBe("Contact *******2671 immediately.");
    }

    [Fact]
    public void Sanitize_RedactsPlainTokens()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(
            "raw token zyxwvutsrqponmlkjihgfedcba");

        sanitized.ShouldBe("raw token <zyx...redacted>");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_ReturnsEmptyForBlankInput(string? input)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldBeEmpty();
    }
}

namespace MessageBridge.Domain.Tests.Privacy;

[Trait("Category", "Unit")]
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
    public void Sanitize_RedactsSecretsWithSeparators()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(
            "{api-key:secret-value; authorization = BearerToken}");

        sanitized.ShouldBe("{[REDACTED_API-KEY]; [REDACTED_AUTHORIZATION]}");
    }

    [Fact]
    public void Sanitize_RedactsConnectionStrings()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(
            "connection_string=localhost");

        sanitized.ShouldNotContain("localhost");
        sanitized.ShouldContain("RED");
    }

    [Theory]
    [InlineData("Username=admin")]
    [InlineData("User ID=admin")]
    [InlineData("UID=admin")]
    public void Sanitize_RedactsConnectionCredentialUserNames(string input)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldNotContain("admin");
        sanitized.ShouldContain("REDACTED");
    }

    [Theory]
    [InlineData("""provider response: {"token":"abc123"}""", "abc123", "REDACTED_TOKEN")]
    [InlineData("""provider response: {"password":"unsafe"}""", "unsafe", "REDACTED_PASSWORD")]
    [InlineData("""provider response: {"authorization":"Bearer private-auth"}""", "Bearer private-auth", "REDACTED_AUTHORIZATION")]
    public void Sanitize_RedactsJsonQuotedSecrets(string input, string secret, string marker)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldStartWith("provider response:");
        sanitized.ShouldNotContain(secret);
        sanitized.ShouldContain(marker);
    }

    [Theory]
    [InlineData("Authorization: Bearer private-auth")]
    [InlineData("request rejected Authorization=Bearer private-auth")]
    [InlineData("header Bearer private-auth was refused")]
    public void Sanitize_RedactsUnquotedAuthorizationCredentials(string input)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldNotContain("private-auth");
        sanitized.ShouldNotContain("Bearer");
        sanitized.ShouldContain("REDACTED_AUTHORIZATION");
    }

    [Theory]
    [InlineData("payload={\"meta\":{\"region\":\"in\"},\"body\":\"private text\"}")]
    [InlineData("payload=[\"private text\",{\"body\":\"private text\"}]")]
    [InlineData("payload={\n  \"body\": \"private text\"\n}")]
    [InlineData("payload=private customer data")]
    public void Sanitize_RedactsCompletePayloadValues(string input)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldBe("payload=[REDACTED_PAYLOAD]");
    }

    [Theory]
    [InlineData("payload=", "payload=[REDACTED_PAYLOAD]")]
    [InlineData("payload=private data; status=failed", "payload=[REDACTED_PAYLOAD]; status=failed")]
    [InlineData("payload=\"unterminated", "payload=[REDACTED_PAYLOAD]")]
    [InlineData("payload=[\"unterminated\"", "payload=[REDACTED_PAYLOAD]")]
    [InlineData("payload={\"body\":\"escaped \\\" quote\"}", "payload=[REDACTED_PAYLOAD]")]
    public void Sanitize_RedactsPayloadValuesAtInputBoundaries(string input, string expected)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("""provider response: {"payload":{"body":"private text","nested":{"token":"inside"}}} completed""")]
    [InlineData("provider response: {\n  \"payload\": {\n    \"body\": \"private text\"\n  }\n} completed")]
    public void Sanitize_RedactsJsonQuotedPayloadProperties(string input)
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(input);

        sanitized.ShouldStartWith("provider response:");
        sanitized.ShouldEndWith(" completed");
        sanitized.ShouldContain("REDACTED_PAYLOAD");
        sanitized.ShouldNotContain("private text");
        sanitized.ShouldNotContain("\"body\"");
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

    [Fact]
    public void Sanitize_RedactsMultipleRecipientTypes()
    {
        var sanitized = MessageBridge.Domain.Privacy.ErrorSanitizer.Sanitize(
            "email=person@example.com phone=+1 (415) 555-2671");

        sanitized.ShouldBe("email=p***n@***.com phone=*******2671");
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

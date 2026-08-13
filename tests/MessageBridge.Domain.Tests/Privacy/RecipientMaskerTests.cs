namespace MessageBridge.Domain.Tests.Privacy;

[Trait("Category", "Unit")]
public class RecipientMaskerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("1234", "****")]
    [InlineData("12-34", "****")]
    public void MaskPhoneNumber_HandlesShortOrBlankValues(string? input, string expected)
    {
        MessageBridge.Domain.Privacy.RecipientMasker.MaskPhoneNumber(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("+1 (415) 555-2671", "*******2671")]
    [InlineData("1234567", "***4567")]
    [InlineData("no-number", "")]
    public void MaskPhoneNumber_MasksValues(string input, string expected)
    {
        var masked = MessageBridge.Domain.Privacy.RecipientMasker.MaskPhoneNumber(input);

        masked.ShouldBe(expected);
    }

    [Theory]
    [InlineData("person@example.com", "p***n@***.com")]
    [InlineData("ab@example.org", "a***@***.org")]
    [InlineData("a@example.net", "a***@***.net")]
    public void MaskEmailAddress_MasksValues(string input, string expected)
    {
        var masked = MessageBridge.Domain.Privacy.RecipientMasker.MaskEmailAddress(input);

        masked.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    [InlineData("missing-at", "***")]
    [InlineData("@example.com", "***")]
    [InlineData("user@example", "***")]
    public void MaskEmailAddress_ReturnsFallbackForInvalidValues(string? input, string expected)
    {
        MessageBridge.Domain.Privacy.RecipientMasker.MaskEmailAddress(input).ShouldBe(expected);
    }
}

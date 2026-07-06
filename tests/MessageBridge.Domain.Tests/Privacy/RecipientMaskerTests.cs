namespace MessageBridge.Domain.Tests.Privacy;

public class RecipientMaskerTests
{
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
}

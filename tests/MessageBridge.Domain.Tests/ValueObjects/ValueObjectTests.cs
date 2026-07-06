namespace MessageBridge.Domain.Tests.ValueObjects;

public class TenantIdTests
{
    public static IEnumerable<object[]> TenantIdInvalidValues => new[]
    {
        new object[] { "" },
        new object[] { "x" },
        new object[] { "tenant abc" },
        new object[] { new string('a', 101) },
    };

    [Fact]
    public void TenantId_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.TenantId.Create("tenant_abc-1");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("tenant_abc-1");
    }

    [Theory]
    [MemberData(nameof(TenantIdInvalidValues))]
    public void TenantId_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.TenantId.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class MessageIdTests
{
    [Fact]
    public void MessageId_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.MessageId.Create("msg_2026_07");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("msg_2026_07");
    }

    [Theory]
    [InlineData("")]
    [InlineData("msg")]
    [InlineData("msg#1")]
    public void MessageId_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.MessageId.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class CorrelationIdTests
{
    [Fact]
    public void CorrelationId_WhenValid_ShouldSucceed()
    {
        var input = Guid.NewGuid().ToString();
        var result = MessageBridge.Domain.ValueObjects.CorrelationId.Create(input);

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void CorrelationId_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.CorrelationId.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class PhoneNumberTests
{
    [Fact]
    public void PhoneNumber_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.PhoneNumber.Create("+1 (415) 555-2671");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("14155552671");
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abc-def-ghi")]
    public void PhoneNumber_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.PhoneNumber.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class EmailAddressTests
{
    [Fact]
    public void EmailAddress_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.EmailAddress.Create("person+alias@example.com");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("person+alias@example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-at-symbol")]
    [InlineData("invalid@domain")]
    public void EmailAddress_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.EmailAddress.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class TemplateNameTests
{
    [Fact]
    public void TemplateName_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.TemplateName.Create("welcome_template");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("welcome_template");
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Template-Name")]
    public void TemplateName_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.TemplateName.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

public class ConfirmationTokenTests
{
    [Fact]
    public void ConfirmationToken_WhenValid_ShouldSucceed()
    {
        var result = MessageBridge.Domain.ValueObjects.ConfirmationToken.Create("ABCDEF1234567890token");

        result.IsError.ShouldBeFalse();
        result.Value.Value.ShouldBe("ABCDEF1234567890token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("bad token with spaces")]
    public void ConfirmationToken_WhenInvalid_ShouldFail(string input)
    {
        var result = MessageBridge.Domain.ValueObjects.ConfirmationToken.Create(input);

        result.IsError.ShouldBeTrue();
    }
}

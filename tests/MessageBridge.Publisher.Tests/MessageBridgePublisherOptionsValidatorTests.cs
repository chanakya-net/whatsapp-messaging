using FluentAssertions;
using MessageBridge.Publisher;
using MessageBridge.Publisher.Internal;

namespace MessageBridge.Publisher.Tests;

[Trait("Category", "Unit")]
public sealed class MessageBridgePublisherOptionsValidatorTests
{
    private readonly MessageBridgePublisherOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithoutDefaultTenant_Fails()
    {
        var result = _validator.Validate(null, new MessageBridgePublisherOptions { DefaultTenantId = "" });

        result.Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "whatsapp.send", "email.confirmation")]
    [InlineData("exchange", "", "email.confirmation")]
    [InlineData("exchange", "whatsapp.send", "")]
    public void Validate_WithoutExchangeOrRoutingKey_Fails(
        string exchange,
        string whatsAppRoutingKey,
        string emailRoutingKey)
    {
        var result = _validator.Validate(null, new MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
            ExchangeName = exchange,
            WhatsAppRoutingKey = whatsAppRoutingKey,
            EmailRoutingKey = emailRoutingKey,
        });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyAllowedTenant_Fails()
    {
        var result = _validator.Validate(null, new MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
            AllowedTenantIds = ["tenant", ""],
        });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithValidOptions_Succeeds()
    {
        var result = _validator.Validate(null, new MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
            AllowedTenantIds = ["tenant"],
        });

        result.Succeeded.Should().BeTrue();
    }
}

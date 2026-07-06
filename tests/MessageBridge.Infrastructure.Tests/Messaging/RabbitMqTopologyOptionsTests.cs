using FluentValidation.Results;
using MessageBridge.Infrastructure.Messaging.Options;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Messaging;

public sealed class RabbitMqTopologyOptionsTests
{
    // --- MessageBridgeTopologyOptions ---

    [Fact]
    public void ExchangeName_WithoutPrefix_ReturnsBareBaseName()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = string.Empty };
        opts.ExchangeName("whatsapp.outbound").ShouldBe("whatsapp.outbound");
    }

    [Fact]
    public void ExchangeName_WithPrefix_ReturnsPrefixedName()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "prod" };
        opts.ExchangeName("whatsapp.outbound").ShouldBe("prod.whatsapp.outbound");
    }

    [Fact]
    public void QueueName_WithoutPrefix_ReturnsBareBaseName()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = string.Empty };
        opts.QueueName("email.confirmations").ShouldBe("email.confirmations");
    }

    [Fact]
    public void QueueName_WithPrefix_ReturnsPrefixedName()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "staging" };
        opts.QueueName("email.confirmations").ShouldBe("staging.email.confirmations");
    }

    [Fact]
    public void RoutingKey_ReturnsLowercaseMessageType()
    {
        var opts = new MessageBridgeTopologyOptions();
        opts.RoutingKey("SendWhatsAppMessage").ShouldBe("sendwhatsappmessage");
    }

    [Fact]
    public void RoutingKey_IsNotPrefixed_EvenWhenPrefixSet()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "prod" };
        opts.RoutingKey("SendEmailConfirmation").ShouldBe("sendemailconfirmation");
    }

    [Fact]
    public void ExchangeName_EmptyName_ThrowsArgumentException()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "prod" };
        Should.Throw<ArgumentException>(() => opts.ExchangeName(string.Empty));
    }

    [Fact]
    public void RoutingKey_EmptyMessageType_ThrowsArgumentException()
    {
        var opts = new MessageBridgeTopologyOptions();
        Should.Throw<ArgumentException>(() => opts.RoutingKey(string.Empty));
    }

    // --- RabbitMqOptionsValidator ---

    [Fact]
    public void Validator_ValidConnectionString_Passes()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions { ConnectionString = "amqps://user:pass@host/vhost" };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_InvalidConnectionStringScheme_Fails()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions { ConnectionString = "rabbitmq://host" };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("amqp://"));
    }

    [Fact]
    public void Validator_DecomposedSettings_AllPresent_Passes()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            Host = "rabbit.internal",
            Port = 5672,
            VirtualHost = "/",
            Username = "guest",
            Password = "guest"
        };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_DecomposedSettings_MissingUsername_Fails()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            Host = "rabbit.internal",
            Password = "secret"
        };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(RabbitMqOptions.Username));
    }

    [Fact]
    public void Validator_DecomposedSettings_MissingPassword_Fails()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            Host = "rabbit.internal",
            Username = "guest"
        };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(RabbitMqOptions.Password));
    }

    [Fact]
    public void RabbitMqOptions_UsesConnectionString_TrueWhenSet()
    {
        var opts = new RabbitMqOptions { ConnectionString = "amqp://localhost" };
        opts.UsesConnectionString.ShouldBeTrue();
    }

    [Fact]
    public void RabbitMqOptions_UsesConnectionString_FalseWhenAbsent()
    {
        var opts = new RabbitMqOptions();
        opts.UsesConnectionString.ShouldBeFalse();
    }
}

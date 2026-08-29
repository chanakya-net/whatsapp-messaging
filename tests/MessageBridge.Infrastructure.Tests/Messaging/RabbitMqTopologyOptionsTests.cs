using FluentValidation.Results;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Messaging;

[Trait("Category", "Unit")]
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

    [Theory]
    [InlineData("dev", "dev.whatsapp.outbound")]
    [InlineData("prod", "prod.whatsapp.outbound")]
    public void ExchangeName_UsesEnvironmentSpecificPrefix(string prefix, string expected)
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = prefix };

        opts.ExchangeName("whatsapp.outbound").ShouldBe(expected);
    }

    [Fact]
    public void Durable_DefaultsToTrue()
    {
        new MessageBridgeTopologyOptions().Durable.ShouldBeTrue();
    }

    [Fact]
    public void Durable_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:Topology:Durable"] = "false"
            })
            .Build();
        var options = config.GetSection(MessageBridgeTopologyOptions.SectionName)
            .Get<MessageBridgeTopologyOptions>();

        options.ShouldNotBeNull();
        options.Durable.ShouldBeFalse();
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

    [Fact]
    public void Validator_TlsConnectionString_Passes()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions { ConnectionString = "amqps://secure.broker.com" };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_InvalidScheme_DetectsError()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions { ConnectionString = "http://localhost" };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validator_BothConnectionStringAndDecomposed_ConnectionStringTakesPrecedence()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            ConnectionString = "amqp://host1:5672",
            Host = string.Empty,
            Username = null,
            Password = null,
            Port = 0
        };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeTrue();
        opts.UsesConnectionString.ShouldBeTrue();
    }

    [Fact]
    public void Validator_DecomposedSettings_WithTls_Passes()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            Host = "secure.rabbit.internal",
            Port = 5671,
            Username = "guest",
            Password = "guest",
            UseSsl = true
        };

        validator.Validate(opts).IsValid.ShouldBeTrue();
        opts.UseSsl.ShouldBeTrue();
        opts.Port.ShouldBe((ushort)5671);
    }

    [Fact]
    public void Validator_DecomposedSettings_WithZeroPort_Fails()
    {
        var result = new RabbitMqOptionsValidator().Validate(new RabbitMqOptions
        {
            Host = "rabbit.internal",
            Username = "guest",
            Password = "guest",
            Port = 0
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.Port));
    }

    [Fact]
    public void Validator_DecomposedSettings_WithDefaultHost_Passes()
    {
        var validator = new RabbitMqOptionsValidator();
        var opts = new RabbitMqOptions
        {
            Username = "guest",
            Password = "guest",
            Port = 5672
        };

        ValidationResult result = validator.Validate(opts);

        result.IsValid.ShouldBeTrue();
        opts.Host.ShouldBe("localhost");
    }

    [Fact]
    public void QueueName_EmptyBaseName_ThrowsArgumentException()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "prod" };
        Should.Throw<ArgumentException>(() => opts.QueueName(string.Empty));
    }

    [Fact]
    public void ExchangeName_WithMultipleSegments_RetainsStructure()
    {
        var opts = new MessageBridgeTopologyOptions { EnvironmentPrefix = "staging" };
        opts.ExchangeName("messages.events.processed").ShouldBe("staging.messages.events.processed");
    }

    [Fact]
    public void RoutingKey_WithNumbers_PreservesCase()
    {
        var opts = new MessageBridgeTopologyOptions();
        opts.RoutingKey("SendNotification2FA").ShouldBe("sendnotification2fa");
    }
}

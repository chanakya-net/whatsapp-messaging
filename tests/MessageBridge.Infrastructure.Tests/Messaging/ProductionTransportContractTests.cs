using FluentValidation.Results;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Messaging;

[Trait("Category", "Unit")]
public sealed class ProductionTransportContractTests
{
    private const string SecretConnectionString = "amqp://user:super-secret@rabbit.example/vhost";

    [Fact]
    public void Validator_SecureMode_AcceptsAmqpsConnectionString()
    {
        var result = new RabbitMqOptionsValidator(requireSecureTransport: true).Validate(
            new RabbitMqOptions { ConnectionString = "amqps://rabbit.example/vhost" });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_SecureMode_RejectsPlaintextConnectionString_WithoutSecret()
    {
        var result = new RabbitMqOptionsValidator(requireSecureTransport: true).Validate(
            new RabbitMqOptions { ConnectionString = SecretConnectionString });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotContain(error => error.ErrorMessage.Contains(SecretConnectionString));
        result.Errors.ShouldNotContain(error => error.ErrorMessage.Contains("super-secret"));
    }

    [Fact]
    public void Validator_SecureMode_RejectsDecomposedPlaintext()
    {
        var result = new RabbitMqOptionsValidator(requireSecureTransport: true).Validate(
            new RabbitMqOptions
            {
                Host = "rabbit.example",
                Username = "user",
                Password = "password",
                UseSsl = false
            });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(RabbitMqOptions.UseSsl));
    }

    [Fact]
    public void Validator_DefaultMode_AcceptsLocalPlaintext()
    {
        ValidationResult result = new RabbitMqOptionsValidator().Validate(
            new RabbitMqOptions { ConnectionString = "amqp://localhost" });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ValidateOptions_Production_RejectsPlaintext_WithoutSecret()
    {
        var result = new RabbitMqValidateOptions(ProductionEnvironment).Validate(
            Options.DefaultName,
            new RabbitMqOptions { ConnectionString = SecretConnectionString });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotContain(failure => failure.Contains(SecretConnectionString));
        result.Failures.ShouldNotContain(failure => failure.Contains("super-secret"));
    }

    [Fact]
    public void ValidateOptions_Production_AcceptsAmqps()
    {
        var result = new RabbitMqValidateOptions(ProductionEnvironment).Validate(
            Options.DefaultName,
            new RabbitMqOptions { ConnectionString = "amqps://rabbit.example/vhost" });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ValidateOptions_WithoutEnvironment_AllowsLocalPlaintext()
    {
        var result = new RabbitMqValidateOptions().Validate(
            Options.DefaultName,
            new RabbitMqOptions { ConnectionString = "amqp://localhost" });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Registration_Production_PlaintextRejected_WithoutSecret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = SecretConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(ProductionEnvironment);
        services.AddMessageBridgeMassTransit(configuration);

        using var provider = services.BuildServiceProvider();
        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        exception.Message.ShouldNotContain(SecretConnectionString);
        exception.Message.ShouldNotContain("super-secret");
    }

    private static IHostEnvironment ProductionEnvironment { get; } = new TestHostEnvironment();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "MessageBridge.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

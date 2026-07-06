using System.Net;
using Google.Protobuf.WellKnownTypes;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using MessageBridge.Infrastructure.Messaging.Options;
using Wolverine;
using Shouldly;
using Xunit;

namespace MessageBridge.Worker.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task Host_Maps_Only_Live_And_Ready_Health_Endpoints()
    {
        await using var factory = BuildWorkerFactory(ValidRabbitMqSettings());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Host_Registers_MassTransit_Consumers_And_Wolverine()
    {
        using var factory = BuildWorkerFactory(ValidRabbitMqSettings());
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetService<SendWhatsAppMessageConsumer>().ShouldNotBeNull();
        services.GetService<SendEmailConfirmationConsumer>().ShouldNotBeNull();
        services.GetService<IMessageBus>().ShouldNotBeNull();
        services.GetService<IOptions<RabbitMqOptions>>().ShouldNotBeNull();
    }

    [Fact]
    public void Contract_Maps_To_SendWhatsAppMessage_Command_With_Normalized_Values()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-1",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Alex" },
            CorrelationId = " ",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
        };

        var command = contract.ToApplicationCommand();

        command.MessageId.ShouldBe("msg-1");
        command.TemplateParameters.ShouldNotBeNull();
        command.CorrelationId.ShouldBeNull();
        command.RequestedAtUtc.ShouldBe(requestedAt);
        command.TemplateParameters!["name"].ShouldBe("Alex");
    }

    [Fact]
    public void Contract_Maps_To_SendEmailConfirmation_Command_With_Normalized_Values()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var expiresAt = requestedAt.AddHours(2);
        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "msg-2",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = string.Empty,
            ConfirmationToken = "token",
            CorrelationId = "",
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAt),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
        };

        var command = contract.ToApplicationCommand();

        command.RecipientName.ShouldBeNull();
        command.CorrelationId.ShouldBeNull();
        command.ExpiresAtUtc.ShouldBe(expiresAt);
    }

    [Fact]
    public void Host_Fails_To_Start_With_Invalid_RabbitMq_Options()
    {
        using var factory = BuildWorkerFactory(new Dictionary<string, string?>
        {
            ["RabbitMq:ConnectionString"] = "rabbitmq://bad-scheme"
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static MessageBridgeWorkerFactory BuildWorkerFactory(
        IReadOnlyDictionary<string, string?> values)
        => new(values);

    private static Dictionary<string, string?> ValidRabbitMqSettings() =>
        new()
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest"
        };

    private sealed class MessageBridgeWorkerFactory(IReadOnlyDictionary<string, string?> values)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(values);
            });
        }
    }
}

using FluentAssertions;
using MessageBridge.Contracts.V1;
using MessageBridge.Publisher.Internal;
using MessageBridge.Publisher.Requests;

namespace MessageBridge.Publisher.Tests;

[Trait("Category", "Unit")]
public sealed class MessageBridgePayloadSerializerTests
{
    [Fact]
    public void SerializeWhatsApp_UsesEmptyStringsForMissingOptionalIds()
    {
        var payload = MessageBridgePayloadSerializer.SerializeWhatsApp(new SendWhatsAppMessageRequest
        {
            PhoneNumber = "+1234567890",
            TemplateId = "template",
            Body = "body",
        });

        var command = SendWhatsAppMessageCommand.Parser.ParseFrom(payload);

        command.MessageId.Should().BeEmpty();
        command.TenantId.Should().BeEmpty();
        command.CorrelationId.Should().BeEmpty();
    }

    [Fact]
    public void SerializeEmail_UsesEmptyStringsForMissingOptionalIds()
    {
        var payload = MessageBridgePayloadSerializer.SerializeEmail(new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "code",
        });

        var command = SendEmailConfirmationCommand.Parser.ParseFrom(payload);

        command.MessageId.Should().BeEmpty();
        command.TenantId.Should().BeEmpty();
        command.CorrelationId.Should().BeEmpty();
    }
}

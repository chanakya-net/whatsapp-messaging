using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MessageBridge.Contracts.V1;
using MessageBridge.Infrastructure.Messaging;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests.Messaging;

[Trait("Category", "Unit")]
public sealed class MessageBridgeSerializationTests
{
    [Fact]
    public void Registry_Contains_Both_Command_Descriptors()
    {
        MessageBridgeCommandRegistry.All.Count.ShouldBe(2);
        MessageBridgeCommandRegistry.GetRequired<SendWhatsAppMessageCommand>()
            .ContractType.ShouldBe(typeof(SendWhatsAppMessageCommand));
        MessageBridgeCommandRegistry.GetRequired<SendEmailConfirmationCommand>()
            .ContractType.ShouldBe(typeof(SendEmailConfirmationCommand));
    }

    [Fact]
    public void WhatsAppCommand_RoundTrips_With_Transport_Metadata()
    {
        var original = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-123",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { { "first_name", "Alex" }, { "code", "123" } },
            CorrelationId = "corr-456",
            RequestedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 1, 1, 12, 0, 0), DateTimeKind.Utc))
        };

        var payload = MessageBridgeCommandSerialization.Serialize(original);

        payload.Descriptor.ShouldBe(MessageBridgeCommandRegistry.GetRequired<SendWhatsAppMessageCommand>());
        payload.Headers[MessageBridgeHeaders.ContentTypeHeader].ShouldBe(MessageBridgeHeaders.ProtobufContentType);
        payload.Headers[MessageBridgeHeaders.CommandHeader].ShouldBe(nameof(SendWhatsAppMessageCommand));
        payload.Headers[MessageBridgeHeaders.MessageUrnHeader].ShouldBe("urn:message:MessageBridge.Contracts.V1:SendWhatsAppMessageCommand");

        var deserialized = MessageBridgeCommandSerialization.Deserialize<SendWhatsAppMessageCommand>(payload.Body);
        var nonGenericDeserialized = MessageBridgeCommandSerialization.Deserialize(payload.Body, payload.Descriptor);

        deserialized.MessageId.ShouldBe(original.MessageId);
        deserialized.TenantId.ShouldBe(original.TenantId);
        deserialized.RecipientPhoneNumber.ShouldBe(original.RecipientPhoneNumber);
        deserialized.TemplateName.ShouldBe(original.TemplateName);
        deserialized.TemplateLanguage.ShouldBe(original.TemplateLanguage);
        deserialized.TemplateParameters["first_name"].ShouldBe(original.TemplateParameters["first_name"]);
        deserialized.TemplateParameters["code"].ShouldBe(original.TemplateParameters["code"]);
        deserialized.CorrelationId.ShouldBe(original.CorrelationId);
        deserialized.RequestedAtUtc.Seconds.ShouldBe(original.RequestedAtUtc.Seconds);
        deserialized.RequestedAtUtc.Nanos.ShouldBe(original.RequestedAtUtc.Nanos);
        nonGenericDeserialized.ShouldBeOfType<SendWhatsAppMessageCommand>();
        ((SendWhatsAppMessageCommand)nonGenericDeserialized).MessageId.ShouldBe(original.MessageId);
    }

    [Fact]
    public void EmailCommand_RoundTrips_With_Transport_Metadata()
    {
        var requestedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 1, 2, 8, 30, 0), DateTimeKind.Utc));
        var expiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 1, 3, 8, 30, 0), DateTimeKind.Utc));

        var original = new SendEmailConfirmationCommand
        {
            MessageId = "email-123",
            TenantId = "tenant-1",
            RecipientEmail = "user@example.com",
            RecipientName = "John Doe",
            ConfirmationToken = "token-abc123xyz",
            CorrelationId = "corr-789",
            RequestedAtUtc = requestedAt,
            ExpiresAtUtc = expiresAt
        };

        var payload = MessageBridgeCommandSerialization.Serialize(original);
        var nonGenericPayload = MessageBridgeCommandSerialization.Serialize((IMessage)original);
        var deserialized = MessageBridgeCommandSerialization.Deserialize<SendEmailConfirmationCommand>(payload.Body);

        payload.Descriptor.ShouldBe(MessageBridgeCommandRegistry.GetRequired<SendEmailConfirmationCommand>());
        payload.Headers[MessageBridgeHeaders.ContentTypeHeader].ShouldBe(MessageBridgeHeaders.ProtobufContentType);
        payload.Headers[MessageBridgeHeaders.CommandHeader].ShouldBe(nameof(SendEmailConfirmationCommand));
        payload.Headers[MessageBridgeHeaders.MessageUrnHeader].ShouldBe("urn:message:MessageBridge.Contracts.V1:SendEmailConfirmationCommand");
        nonGenericPayload.Descriptor.ShouldBe(payload.Descriptor);
        nonGenericPayload.Body.ShouldBe(payload.Body);
        nonGenericPayload.Headers.ShouldBe(payload.Headers);

        deserialized.MessageId.ShouldBe(original.MessageId);
        deserialized.TenantId.ShouldBe(original.TenantId);
        deserialized.RecipientEmail.ShouldBe(original.RecipientEmail);
        deserialized.RecipientName.ShouldBe(original.RecipientName);
        deserialized.ConfirmationToken.ShouldBe(original.ConfirmationToken);
        deserialized.CorrelationId.ShouldBe(original.CorrelationId);
        deserialized.RequestedAtUtc.Seconds.ShouldBe(original.RequestedAtUtc.Seconds);
        deserialized.RequestedAtUtc.Nanos.ShouldBe(original.RequestedAtUtc.Nanos);
        deserialized.ExpiresAtUtc.Seconds.ShouldBe(original.ExpiresAtUtc.Seconds);
        deserialized.ExpiresAtUtc.Nanos.ShouldBe(original.ExpiresAtUtc.Nanos);
    }

    [Fact]
    public void Registry_Throws_For_Unregistered_Message_Type()
    {
        Should.Throw<KeyNotFoundException>(() => MessageBridgeCommandRegistry.GetRequired<Timestamp>());
    }

    [Fact]
    public void Descriptor_Throws_When_Message_Type_Does_Not_Match()
    {
        var descriptor = MessageBridgeCommandRegistry.GetRequired<SendWhatsAppMessageCommand>();
        var message = new SendEmailConfirmationCommand();

        Should.Throw<ArgumentException>(() => descriptor.Serialize(message));
    }

    [Fact]
    public void NonGeneric_Serialize_Handles_IMessage_Interface()
    {
        IMessage message = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-generic",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "alert",
            TemplateLanguage = "en",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var payload = MessageBridgeCommandSerialization.Serialize(message);

        payload.ShouldNotBeNull();
        payload.Body.ShouldNotBeEmpty();
        payload.Descriptor.ContractType.ShouldBe(typeof(SendWhatsAppMessageCommand));
    }

    [Fact]
    public void NonGeneric_Deserialize_WithDescriptor_RoundTrips()
    {
        var original = new SendEmailConfirmationCommand
        {
            MessageId = "email-roundtrip",
            TenantId = "tenant-1",
            RecipientEmail = "test@example.com",
            ConfirmationToken = "token-rt",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1))
        };

        var payload = MessageBridgeCommandSerialization.Serialize(original);
        var deserialized = MessageBridgeCommandSerialization.Deserialize(payload.Body, payload.Descriptor);

        deserialized.ShouldBeOfType<SendEmailConfirmationCommand>();
        var email = (SendEmailConfirmationCommand)deserialized;
        email.MessageId.ShouldBe(original.MessageId);
        email.RecipientEmail.ShouldBe(original.RecipientEmail);
    }

    [Fact]
    public void Serialization_Produces_Consistent_Headers()
    {
        var cmd1 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-hdr-1",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
        var cmd2 = new SendWhatsAppMessageCommand
        {
            MessageId = "msg-hdr-2",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15559876543",
            TemplateName = "confirm",
            TemplateLanguage = "es",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var payload1 = MessageBridgeCommandSerialization.Serialize(cmd1);
        var payload2 = MessageBridgeCommandSerialization.Serialize(cmd2);

        payload1.Headers.Keys.ShouldBe(payload2.Headers.Keys);
        payload1.Headers[MessageBridgeHeaders.ContentTypeHeader].ShouldBe(payload2.Headers[MessageBridgeHeaders.ContentTypeHeader]);
        payload1.Headers[MessageBridgeHeaders.CommandHeader].ShouldBe(nameof(SendWhatsAppMessageCommand));
        payload2.Headers[MessageBridgeHeaders.CommandHeader].ShouldBe(nameof(SendWhatsAppMessageCommand));
    }

    [Fact]
    public void Generic_Deserialize_Throws_For_Invalid_Bytes_Without_Leaking_Input()
    {
        var secret = "secret-token-not-a-valid-payload";

        var exception = Should.Throw<InvalidProtocolBufferException>(() =>
            MessageBridgeCommandSerialization.Deserialize<SendEmailConfirmationCommand>(
                System.Text.Encoding.UTF8.GetBytes(secret)));

        exception.Message.ShouldNotContain(secret);
    }

    [Fact]
    public void NonGeneric_Deserialize_Throws_For_Invalid_Bytes_Through_Adapter()
    {
        var descriptor = MessageBridgeCommandRegistry.GetRequired<SendWhatsAppMessageCommand>();

        Should.Throw<InvalidProtocolBufferException>(() =>
            MessageBridgeCommandSerialization.Deserialize(new byte[] { 0xFF, 0xFF }, descriptor));
    }
}

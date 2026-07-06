using System.Diagnostics;
using System.IO;
using FluentAssertions;
using MessageBridge.Publisher.Requests;
using MessageBridge.Publisher.Validation;
using MessageBridge.Publisher.Internal;
using MessageBridge.Publisher.Internal.Contracts;
using Microsoft.Extensions.Options;
using ProtoBuf;
using Xunit;

namespace MessageBridge.Publisher.Tests;

public sealed class MessageBridgePublisherTests
{
    [Fact]
    public async Task PublishWhatsAppMessage_PublishesProtobufPayloadAndMetadata()
    {
        var fakeTransport = new FakeTransport();
        var subject = BuildPublisher(fakeTransport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant-default",
            WhatsAppRoutingKey = "messagebridge.whatsapp.send",
        });

        var request = new SendWhatsAppMessageRequest
        {
            PhoneNumber = "+1234567890",
            TemplateId = "tmpl-001",
            Body = "hello",
            LanguageCode = "en-US",
        };

        var result = await subject.PublishWhatsAppMessageAsync(request);
        result.IsError.Should().BeFalse();

        var payload = DeserializeWhatsApp(fakeTransport.LastEnvelope!.Payload);
        payload.MessageId.Should().Be(result.Value.MessageId);
        payload.TenantId.Should().Be("tenant-default");
        payload.PhoneNumber.Should().Be("+1234567890");

        fakeTransport.LastEnvelope!.ExchangeName.Should().Be("messagebridge.commands");
        fakeTransport.LastEnvelope.RoutingKey.Should().Be("messagebridge.whatsapp.send");
        fakeTransport.LastEnvelope.Headers["x-command-type"].Should().Be("whatsapp.send");
        fakeTransport.LastEnvelope.Headers["x-format"].Should().Be("protobuf");
    }

    [Fact]
    public async Task PublishEmailConfirmation_GeneratesDefaultsAndRespectsTenantRules()
    {
        var fakeTransport = new FakeTransport();
        var subject = BuildPublisher(fakeTransport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant-allowed",
            AllowedTenantIds = new[] { "tenant-allowed", "tenant-other" },
            EmailRoutingKey = "messagebridge.email.confirm",
        });

        using var activity = new Activity("activity");
        activity.Start();

        var request = new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "987654",
        };

        var result = await subject.PublishEmailConfirmationAsync(request);

        result.IsError.Should().BeFalse();
        result.Value.CorrelationId.Should().Be(activity.TraceId.ToString());
        result.Value.MessageId.Should().HaveLength(26);
        fakeTransport.LastEnvelope.Should().NotBeNull();
        fakeTransport.LastEnvelope!.RoutingKey.Should().Be("messagebridge.email.confirm");
        fakeTransport.LastEnvelope.Headers["x-command-type"].Should().Be("email.confirmation");

        activity.Stop();
    }

    [Fact]
    public async Task PublishWithUnknownTenant_ReturnsValidationError()
    {
        var fakeTransport = new FakeTransport();
        var subject = BuildPublisher(fakeTransport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant-default",
            AllowedTenantIds = new[] { "tenant-allowed" },
        });

        var request = new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "abc",
            TenantId = "tenant-bad",
        };

        var result = await subject.PublishEmailConfirmationAsync(request);
        result.IsError.Should().BeTrue();
        fakeTransport.LastEnvelope.Should().BeNull();
        result.Errors.Should().Contain(error => error.Description.Contains("not allowed"));
    }

    private static MessageBridge.Publisher.IMessageBridgePublisher BuildPublisher(
        FakeTransport transport,
        MessageBridge.Publisher.MessageBridgePublisherOptions options)
    {
        var resolvedOptions = Options.Create(options);
        return new MessageBridge.Publisher.DirectMessageBridgePublisher(
            resolvedOptions,
            new SendWhatsAppMessageRequestValidator(),
            new SendEmailConfirmationRequestValidator(),
            transport);
    }

    private static SendWhatsAppMessageContract DeserializeWhatsApp(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        return Serializer.Deserialize<SendWhatsAppMessageContract>(stream);
    }

    private sealed class FakeTransport : IMessageBridgePublisherTransport
    {
        public MessageBridgePublisherEnvelope? LastEnvelope { get; private set; }

        public Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken)
        {
            LastEnvelope = envelope;
            return Task.CompletedTask;
        }
    }
}

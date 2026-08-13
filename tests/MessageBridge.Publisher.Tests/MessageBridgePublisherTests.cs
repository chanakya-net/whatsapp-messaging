using System.Diagnostics;
using FluentAssertions;
using MessageBridge.Contracts.V1;
using MessageBridge.Publisher.Requests;
using MessageBridge.Publisher.Validation;
using MessageBridge.Publisher.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageBridge.Publisher.Tests;

[Trait("Category", "Unit")]
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

        var payload = SendWhatsAppMessageCommand.Parser.ParseFrom(fakeTransport.LastEnvelope!.Payload);
        payload.MessageId.Should().Be(result.Value.MessageId);
        payload.TenantId.Should().Be("tenant-default");
        payload.RecipientPhoneNumber.Should().Be("+1234567890");
        payload.TemplateName.Should().Be("tmpl-001");
        payload.TemplateParameters["body"].Should().Be("hello");

        fakeTransport.LastEnvelope!.ExchangeName.Should().Be("messagebridge.commands");
        fakeTransport.LastEnvelope.RoutingKey.Should().Be("messagebridge.whatsapp.send");
        fakeTransport.LastEnvelope.Headers["Content-Type"].Should().Be("application/x-protobuf");
        fakeTransport.LastEnvelope.Headers["MessageBridge-Command"].Should().Be("SendWhatsAppMessageCommand");
        fakeTransport.LastEnvelope.Headers["MessageBridge-MessageUrn"].Should().Be("urn:message:MessageBridge.Contracts.V1:SendWhatsAppMessageCommand");
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
        fakeTransport.LastEnvelope.Headers["MessageBridge-Command"].Should().Be("SendEmailConfirmationCommand");

        var payload = SendEmailConfirmationCommand.Parser.ParseFrom(fakeTransport.LastEnvelope.Payload);
        payload.RecipientEmail.Should().Be("user@example.com");
        payload.ConfirmationToken.Should().Be("987654");

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

    [Fact]
    public async Task PublishWhatsAppMessage_WithNullRequest_ReturnsValidationError()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var result = await publisher.PublishWhatsAppMessageAsync(null!);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(error => error.Code == "publisher.request");
        transport.LastEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task PublishEmailConfirmation_WithNullRequest_ReturnsValidationError()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var result = await publisher.PublishEmailConfirmationAsync(null!);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(error => error.Code == "publisher.request");
        transport.LastEnvelope.Should().BeNull();
    }

    [Theory]
    [InlineData("", "template", "body", "en-US")]
    [InlineData("+1234567890", "", "body", "en-US")]
    [InlineData("+1234567890", "template", "", "en-US")]
    [InlineData("+1234567890", "template", "body", "")]
    public async Task PublishWhatsAppMessage_WithInvalidFields_DoesNotPublish(
        string phoneNumber,
        string templateId,
        string body,
        string languageCode)
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
        {
            PhoneNumber = phoneNumber,
            TemplateId = templateId,
            Body = body,
            LanguageCode = languageCode,
        });

        result.IsError.Should().BeTrue();
        transport.LastEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task PublishEmailConfirmation_WithInvalidFields_DoesNotPublish()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var result = await publisher.PublishEmailConfirmationAsync(new SendEmailConfirmationRequest
        {
            Email = "not-an-email",
            ConfirmationCode = string.Empty,
        });

        result.IsError.Should().BeTrue();
        transport.LastEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task PublishWhatsAppMessage_PreservesIdsTenantAndEnvelopeMetadata()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            ExchangeName = "custom.exchange",
            WhatsAppRoutingKey = "custom.whatsapp",
        });

        var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
        {
            TenantId = "tenant-explicit",
            MessageId = "message-explicit",
            CorrelationId = "correlation-explicit",
            PhoneNumber = "+1234567890",
            TemplateId = "welcome",
            Body = "Hello",
            LanguageCode = "en-GB",
        });

        result.Value.Should().Be(new MessageBridge.Publisher.MessageBridgePublisherResult(
            "message-explicit", "correlation-explicit", "tenant-explicit"));
        transport.LastEnvelope!.ExchangeName.Should().Be("custom.exchange");
        transport.LastEnvelope.RoutingKey.Should().Be("custom.whatsapp");
        transport.LastEnvelope.MessageId.Should().Be("message-explicit");
        transport.LastEnvelope.CorrelationId.Should().Be("correlation-explicit");
        transport.LastEnvelope.Headers["x-tenant-id"].Should().Be("tenant-explicit");

        var payload = SendWhatsAppMessageCommand.Parser.ParseFrom(transport.LastEnvelope.Payload);
        payload.MessageId.Should().Be("message-explicit");
        payload.CorrelationId.Should().Be("correlation-explicit");
        payload.TenantId.Should().Be("tenant-explicit");
        payload.RecipientPhoneNumber.Should().Be("+1234567890");
        payload.TemplateName.Should().Be("welcome");
        payload.TemplateLanguage.Should().Be("en-GB");
        payload.TemplateParameters["body"].Should().Be("Hello");
    }

    [Fact]
    public async Task PublishEmailConfirmation_WithoutTenantOrCorrelation_UsesGeneratedDefaults()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant-fallback",
            EmailRoutingKey = "custom.email",
        });

        var result = await publisher.PublishEmailConfirmationAsync(new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "code",
        });

        result.IsError.Should().BeFalse();
        result.Value.TenantId.Should().Be("tenant-fallback");
        result.Value.MessageId.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26}$");
        result.Value.CorrelationId.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26}$");
        transport.LastEnvelope!.ExchangeName.Should().Be("messagebridge.commands");
        transport.LastEnvelope!.RoutingKey.Should().Be("custom.email");
        transport.LastEnvelope.MessageId.Should().Be(result.Value.MessageId);
        transport.LastEnvelope.CorrelationId.Should().Be(result.Value.CorrelationId);
        transport.LastEnvelope.Headers["x-tenant-id"].Should().Be("tenant-fallback");

        var payload = SendEmailConfirmationCommand.Parser.ParseFrom(transport.LastEnvelope.Payload);
        payload.MessageId.Should().Be(result.Value.MessageId);
        payload.CorrelationId.Should().Be(result.Value.CorrelationId);
        payload.TenantId.Should().Be("tenant-fallback");
        payload.RecipientEmail.Should().Be("user@example.com");
        payload.ConfirmationToken.Should().Be("code");
    }

    [Fact]
    public async Task PublishEmailConfirmation_PreservesExplicitMessageId()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var result = await publisher.PublishEmailConfirmationAsync(new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "code",
            MessageId = "email-message",
        });

        result.Value.MessageId.Should().Be("email-message");
        transport.LastEnvelope!.MessageId.Should().Be("email-message");
    }

    [Fact]
    public async Task PublishEmailConfirmation_WithoutEffectiveTenant_ReturnsValidationError()
    {
        var transport = new FakeTransport();
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = string.Empty,
        });

        var result = await publisher.PublishEmailConfirmationAsync(new SendEmailConfirmationRequest
        {
            Email = "user@example.com",
            ConfirmationCode = "code",
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(error => error.Code == "publisher.tenant.required");
        transport.LastEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task PublishEmailConfirmation_PropagatesTransportFailure()
    {
        var transport = new FakeTransport { Exception = new InvalidOperationException("transport failed") };
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var action = () => publisher.PublishEmailConfirmationAsync(ValidEmailRequest());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("transport failed");
    }

    [Fact]
    public async Task PublishEmailConfirmation_PropagatesCancellationToTransport()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeTransport
        {
            CancellationSourceToCancel = cancellation,
            ThrowIfCancelled = true,
        };
        var publisher = BuildPublisher(transport, new MessageBridge.Publisher.MessageBridgePublisherOptions
        {
            DefaultTenantId = "tenant",
        });

        var action = () => publisher.PublishEmailConfirmationAsync(ValidEmailRequest(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        transport.ReceivedCancellationToken.Should().Be(cancellation.Token);
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

    private static SendEmailConfirmationRequest ValidEmailRequest() => new()
    {
        Email = "user@example.com",
        ConfirmationCode = "code",
    };

    private sealed class FakeTransport : IMessageBridgePublisherTransport
    {
        public MessageBridgePublisherEnvelope? LastEnvelope { get; private set; }
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public Exception? Exception { get; init; }
        public bool ThrowIfCancelled { get; init; }
        public CancellationTokenSource? CancellationSourceToCancel { get; init; }

        public Task PublishAsync(MessageBridgePublisherEnvelope envelope, CancellationToken cancellationToken)
        {
            LastEnvelope = envelope;
            ReceivedCancellationToken = cancellationToken;
            CancellationSourceToCancel?.Cancel();
            if (ThrowIfCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }
}

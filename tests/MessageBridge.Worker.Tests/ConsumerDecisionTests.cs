using System.Reflection;
using ErrorOr;
using FluentValidation;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Messages.Validation;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Messaging.Consumers;
using MessageBridge.Infrastructure.Messaging.Mappers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;

namespace MessageBridge.Worker.Tests;

[Trait("Category", "Unit")]
public sealed class ConsumerDecisionTests
{
    [Fact]
    public async Task WhatsAppConsumer_maps_contract_and_records_completion_without_host()
    {
        var store = new RecordingStore();
        var bus = TestMessageBus.Create(new Success());
        var consumer = new SendWhatsAppMessageConsumer(
            new MessageProcessingCoordinator(store, bus.Bus),
            new SendWhatsAppMessageValidator(),
            NullLogger<SendWhatsAppMessageConsumer>.Instance);
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "message-001",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Ada" },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await consumer.Consume(TestConsumeContext<SendWhatsAppMessageCommand>.Create(contract).Context);

        var command = bus.Messages.ShouldHaveSingleItem().ShouldBeOfType<SendWhatsAppMessage>();
        command.TemplateParameters!["name"].ShouldBe("Ada");
        store.Statuses.ShouldContain(ProcessingStatus.Processing);
        store.Statuses.ShouldContain(ProcessingStatus.Completed);
    }

    [Fact]
    public async Task EmailConsumer_rejects_invalid_mapping_without_invoking_handler()
    {
        var store = new RecordingStore();
        var bus = TestMessageBus.Create(new Success());
        var consumer = new SendEmailConfirmationConsumer(
            new MessageProcessingCoordinator(store, bus.Bus),
            new SendEmailConfirmationValidator(),
            NullLogger<SendEmailConfirmationConsumer>.Instance);
        var contract = new SendEmailConfirmationCommand
        {
            MessageId = "message-invalid",
            TenantId = "tenant-1",
            RecipientEmail = "not-an-email",
            ConfirmationToken = string.Empty,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await consumer.Consume(TestConsumeContext<SendEmailConfirmationCommand>.Create(contract).Context);

        bus.Messages.ShouldBeEmpty();
        store.Statuses.ShouldContain(ProcessingStatus.Rejected);
    }

    [Fact]
    public async Task WhatsAppConsumer_surfaces_handler_failure_with_sanitized_details()
    {
        var store = new RecordingStore();
        var bus = TestMessageBus.Create(Error.Failure(
            "Provider.Send",
            "token=super_secret phone=+1 (415) 555-2671"));
        var consumer = new SendWhatsAppMessageConsumer(
            new MessageProcessingCoordinator(store, bus.Bus),
            new SendWhatsAppMessageValidator(),
            NullLogger<SendWhatsAppMessageConsumer>.Instance);
        var contract = new SendWhatsAppMessageCommand
        {
            MessageId = "message-failure",
            TenantId = "tenant-1",
            RecipientPhoneNumber = "+15551234567",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => consumer.Consume(TestConsumeContext<SendWhatsAppMessageCommand>.Create(contract).Context));

        exception.Message.ShouldContain("SendWhatsAppMessageCommand");
        exception.Message.ShouldNotContain("super_secret");
        store.Statuses.ShouldContain(ProcessingStatus.Processing);
    }

    private sealed class RecordingStore : IMessageProcessingStore
    {
        public List<ProcessingStatus> Statuses { get; } = [];

        public Task<CreateMessageProcessingResult> CreateAsync(
            CreateMessageProcessingRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new MessageProcessingSnapshot(
                Guid.NewGuid(), request.MessageId, request.MessageType,
                ProcessingStatus.Received, request.PayloadHash, request.Provider,
                new Dictionary<string, string?>(request.ProviderMetadata), null, 1,
                now, now, null);
            Statuses.Add(ProcessingStatus.Received);
            return Task.FromResult(new CreateMessageProcessingResult(
                CreateMessageProcessingOutcome.Created, snapshot));
        }

        public Task<MessageProcessingSnapshot?> GetAsync(
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MessageProcessingSnapshot?>(null);

        public Task<MessageProcessingSnapshot> UpdateStatusAsync(
            string messageId,
            string messageType,
            ProcessingStatus status,
            string? failureReason = null,
            CancellationToken cancellationToken = default)
        {
            Statuses.Add(status);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MessageProcessingSnapshot(
                Guid.NewGuid(), messageId, messageType, status, string.Empty, string.Empty,
                new Dictionary<string, string?>(), failureReason, 1, now, now, now));
        }
    }

    private class TestMessageBus : DispatchProxy
    {
        private ErrorOr<Success> _response;
        public List<object> Messages { get; } = [];

        public IMessageBus Bus => (IMessageBus)(object)this;

        public static TestMessageBus Create(ErrorOr<Success> response)
        {
            var proxy = Create<IMessageBus, TestMessageBus>();
            var fake = (TestMessageBus)(object)proxy;
            fake._response = response;
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "InvokeAsync")
            {
                Messages.Add(args![0]!);
                return Task.FromResult(_response);
            }

            if (targetMethod?.Name == "get_TenantId")
                return null;

            if (targetMethod?.Name == "set_TenantId")
                return null;

            throw new NotSupportedException($"Unexpected IMessageBus member: {targetMethod?.Name}");
        }
    }

    private class TestConsumeContext<TMessage> : DispatchProxy
        where TMessage : class
    {
        private TMessage? _message;

        public ConsumeContext<TMessage> Context => (ConsumeContext<TMessage>)(object)this;

        public static TestConsumeContext<TMessage> Create(TMessage message)
        {
            var proxy = Create<ConsumeContext<TMessage>, TestConsumeContext<TMessage>>();
            var fake = (TestConsumeContext<TMessage>)(object)proxy;
            fake._message = message;
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Message" => _message,
                "get_CancellationToken" => CancellationToken.None,
                _ => throw new NotSupportedException(
                    $"Unexpected ConsumeContext member: {targetMethod?.Name}")
            };
    }
}

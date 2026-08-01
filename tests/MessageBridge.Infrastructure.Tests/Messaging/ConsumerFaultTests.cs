using System.Reflection;
using ErrorOr;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Messaging.Consumers;
using Shouldly;
using Wolverine;

namespace MessageBridge.Infrastructure.Tests.Messaging;

[Trait("Category", "Unit")]
public sealed class ConsumerFaultTests
{
    [Fact]
    public async Task WhatsAppFaultConsumer_marks_message_failed_without_transport()
    {
        var store = new RecordingStore();
        var consumer = new SendWhatsAppMessageFaultConsumer(
            new MessageProcessingCoordinator(store, NullMessageBus.Create()));
        var message = new SendWhatsAppMessageCommand { MessageId = "message-001" };

        await consumer.Consume(FaultContext<SendWhatsAppMessageCommand>.Create(message));

        store.MessageId.ShouldBe("message-001");
        store.MessageType.ShouldBe(nameof(SendWhatsAppMessageCommand));
        store.Status.ShouldBe(ProcessingStatus.Failed);
    }

    [Fact]
    public async Task EmailFaultConsumer_marks_message_failed_without_transport()
    {
        var store = new RecordingStore();
        var consumer = new SendEmailConfirmationFaultConsumer(
            new MessageProcessingCoordinator(store, NullMessageBus.Create()));
        var message = new SendEmailConfirmationCommand { MessageId = "message-002" };

        await consumer.Consume(FaultContext<SendEmailConfirmationCommand>.Create(message));

        store.MessageId.ShouldBe("message-002");
        store.MessageType.ShouldBe(nameof(SendEmailConfirmationCommand));
        store.Status.ShouldBe(ProcessingStatus.Failed);
    }

    private sealed class RecordingStore : IMessageProcessingStore
    {
        public string? MessageId { get; private set; }
        public string? MessageType { get; private set; }
        public ProcessingStatus Status { get; private set; }

        public Task<CreateMessageProcessingResult> CreateAsync(
            CreateMessageProcessingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            MessageId = messageId;
            MessageType = messageType;
            Status = status;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MessageProcessingSnapshot(
                Guid.NewGuid(), messageId, messageType, status, string.Empty, string.Empty,
                new Dictionary<string, string?>(), failureReason, 1, now, now, now));
        }
    }

    private class NullMessageBus : DispatchProxy
    {
        public static IMessageBus Create()
        {
            var proxy = Create<IMessageBus, NullMessageBus>();
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"Unexpected IMessageBus member: {targetMethod?.Name}");
    }

    private class FaultContext<TMessage> : DispatchProxy
        where TMessage : class
    {
        private MassTransit.Fault<TMessage>? _fault;

        public static ConsumeContext<MassTransit.Fault<TMessage>> Create(TMessage message)
        {
            var proxy = Create<ConsumeContext<MassTransit.Fault<TMessage>>, FaultContext<TMessage>>();
            var fake = (FaultContext<TMessage>)(object)proxy;
            fake._fault = FaultProxy<TMessage>.Create(message);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Message" => _fault!,
                "get_CancellationToken" => CancellationToken.None,
                _ => throw new NotSupportedException(
                    $"Unexpected ConsumeContext member: {targetMethod?.Name}")
            };
    }

    private class FaultProxy<TMessage> : DispatchProxy
        where TMessage : class
    {
        private TMessage? _message;

        public static MassTransit.Fault<TMessage> Create(TMessage message)
        {
            var proxy = Create<MassTransit.Fault<TMessage>, FaultProxy<TMessage>>();
            var fake = (FaultProxy<TMessage>)(object)proxy;
            fake._message = message;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Message" => _message!,
                "get_Exceptions" => Array.Empty<MassTransit.ExceptionInfo>(),
                _ => null
            };
    }
}

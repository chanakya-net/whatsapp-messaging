using System.Security.Cryptography;
using ErrorOr;
using FluentValidation;
using Google.Protobuf;
using MassTransit;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Domain.Processing;
using MessageBridge.Domain.Privacy;
using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Options;
using Wolverine;
using PersistenceStore = MessageBridge.Application.Persistence.IMessageProcessingStore;
using MessageBridge.Contracts.V1;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class MessageProcessingCoordinator(
    PersistenceStore store,
    IMessageBus messageBus,
    ISendEndpointProvider sendEndpointProvider,
    IOptions<MessageBridgeTopologyOptions> topologyOptions,
    IOptions<TransportRetryOptions> transportRetryOptions)
{
    public async Task ConsumeAsync<TContract, TCommand>(
        ConsumeContext<TContract> context,
        TCommand command,
        IValidator<TCommand> validator,
        CancellationToken cancellationToken)
        where TContract : class, Google.Protobuf.IMessage
    {
        ArgumentNullException.ThrowIfNull(command);
        var contract = context.Message;
        var messageId = ReadRequired(contract, "MessageId");
        var messageType = typeof(TContract).Name;

        await store.CreateAsync(
            new MessageBridge.Application.Persistence.CreateMessageProcessingRequest(
                messageId,
                messageType,
                ComputePayloadHash(contract),
                "rabbitmq",
                new Dictionary<string, string?>
                {
                    ["transport"] = "masstransit",
                    ["contract"] = messageType
                }),
            cancellationToken);

        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            var validationErrors = validation.ToErrorOr(command);
            await store.UpdateStatusAsync(
                messageId,
                messageType,
                ProcessingStatus.Rejected,
                ConsumerDispatchFailure.DescribeErrors(validationErrors.Errors),
                cancellationToken);
            return;
        }

        await store.UpdateStatusAsync(
            messageId,
            messageType,
            ProcessingStatus.Processing,
            cancellationToken: cancellationToken);

        var result = await messageBus.InvokeAsync<ErrorOr<Success>>(command, cancellationToken);
        if (!result.IsError)
        {
            await store.UpdateStatusAsync(
                messageId,
                messageType,
                ProcessingStatus.Completed,
                cancellationToken: cancellationToken);
            return;
        }

        if (ConsumerDispatchFailure.IsRejected(result.Errors))
        {
            await store.UpdateStatusAsync(
                messageId,
                messageType,
                ProcessingStatus.Rejected,
                ConsumerDispatchFailure.DescribeErrors(result.Errors),
                cancellationToken);
            return;
        }

        await store.UpdateStatusAsync(
            messageId,
            messageType,
            ProcessingStatus.Failed,
            ConsumerDispatchFailure.DescribeErrors(result.Errors),
            cancellationToken);
        if (transportRetryOptions.Value.EnableErrorQueueForwarding)
        {
            await SendToErrorQueueAsync(contract, cancellationToken);
        }

        throw ConsumerDispatchFailure.CreateException(messageType, result.Errors);
    }

    public Task MarkFailedAsync<TContract>(
        MassTransit.Fault<TContract> fault,
        CancellationToken cancellationToken)
        where TContract : class
    {
        var messageId = ReadRequired(fault.Message, "MessageId");

        return store.UpdateStatusAsync(
            messageId,
            typeof(TContract).Name,
            ProcessingStatus.Failed,
            ConsumerDispatchFailure.DescribeExceptions(ReadExceptions(fault)),
            cancellationToken);
    }

    private static string ComputePayloadHash(Google.Protobuf.IMessage contract)
    {
        var payload = MessageBridgeCommandSerialization.Serialize(contract).Body;
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    private static string ReadRequired(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source) as string;
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{source.GetType().Name}.{propertyName} is required.")
            : value;
    }

    private static IReadOnlyList<(string Type, string Message)> ReadExceptions<TContract>(MassTransit.Fault<TContract> fault)
        where TContract : class
    {
        var property = fault.GetType().GetProperty("Exceptions");
        if (property?.GetValue(fault) is not IEnumerable<object> exceptions)
        {
            return [];
        }

        return
        [
            .. exceptions.Select(exception => (
                ReadOptional(exception, "ExceptionType") ?? "Exception",
                ReadOptional(exception, "Message") ?? string.Empty))
        ];
    }

    private static string? ReadOptional(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName)?.GetValue(source) as string;
    }

    private async Task SendToErrorQueueAsync<TContract>(TContract contract, CancellationToken cancellationToken)
        where TContract : class
    {
        var queueName = contract switch
        {
            SendWhatsAppMessageCommand => "send-whats-app-message_error",
            SendEmailConfirmationCommand => "send-email-confirmation_error",
            _ => null
        };

        if (queueName is null)
        {
            return;
        }

        var prefix = topologyOptions.Value.EnvironmentPrefix;
        var endpoint = await sendEndpointProvider.GetSendEndpoint(
            new Uri($"queue:{BuildQueueName(prefix, queueName)}"));
        await endpoint.Send(contract, cancellationToken);
    }

    private static string BuildQueueName(string prefix, string queueName)
    {
        return string.IsNullOrWhiteSpace(prefix) ? queueName : $"{prefix}-{queueName}";
    }
}

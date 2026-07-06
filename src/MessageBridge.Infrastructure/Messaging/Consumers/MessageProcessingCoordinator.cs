using System.Security.Cryptography;
using ErrorOr;
using FluentValidation;
using Google.Protobuf;
using MassTransit;
using MessageBridge.Application.Common.Validation;
using MessageBridge.Domain.Processing;
using MessageBridge.Domain.Privacy;
using Wolverine;
using PersistenceStore = MessageBridge.Application.Persistence.IMessageProcessingStore;

namespace MessageBridge.Infrastructure.Messaging.Consumers;

public sealed class MessageProcessingCoordinator(
    PersistenceStore store,
    IMessageBus messageBus)
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
        return
        [
            .. fault.Exceptions.Select(exception => (
                exception.ExceptionType ?? "Exception",
                exception.Message ?? string.Empty))
        ];
    }
}

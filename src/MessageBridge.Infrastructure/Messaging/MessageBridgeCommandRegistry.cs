using MessageBridge.Contracts.V1;
using Google.Protobuf;

namespace MessageBridge.Infrastructure.Messaging;

public static class MessageBridgeCommandRegistry
{
    private static readonly MessageBridgeCommandDescriptor[] AllDescriptors =
    [
        MessageBridgeCommandDescriptor.Create<SendWhatsAppMessageCommand>(
            nameof(SendWhatsAppMessageCommand),
            message => message.ToByteArray(),
            bytes => SendWhatsAppMessageCommand.Parser.ParseFrom(bytes)),
        MessageBridgeCommandDescriptor.Create<SendEmailConfirmationCommand>(
            nameof(SendEmailConfirmationCommand),
            message => message.ToByteArray(),
            bytes => SendEmailConfirmationCommand.Parser.ParseFrom(bytes))
    ];

    public static IReadOnlyList<MessageBridgeCommandDescriptor> All => AllDescriptors;

    public static MessageBridgeCommandDescriptor GetRequired<TMessage>()
        where TMessage : class, IMessage
    {
        return GetRequired(typeof(TMessage));
    }

    public static MessageBridgeCommandDescriptor GetRequired(Type messageType)
    {
        return TryGet(messageType, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"No command descriptor is registered for {messageType.FullName}.");
    }

    public static bool TryGet(Type messageType, out MessageBridgeCommandDescriptor descriptor)
    {
        foreach (var item in AllDescriptors)
        {
            if (item.ContractType == messageType)
            {
                descriptor = item;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }
}

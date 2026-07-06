using Google.Protobuf;

namespace MessageBridge.Infrastructure.Messaging;

public sealed record MessageBridgeCommandPayload(
    MessageBridgeCommandDescriptor Descriptor,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);

public static class MessageBridgeCommandSerialization
{
    public static MessageBridgeCommandPayload Serialize<TMessage>(TMessage message)
        where TMessage : class, IMessage
    {
        var descriptor = MessageBridgeCommandRegistry.GetRequired<TMessage>();
        return Serialize(message, descriptor);
    }

    public static MessageBridgeCommandPayload Serialize(IMessage message)
    {
        var descriptor = MessageBridgeCommandRegistry.GetRequired(message.GetType());
        return Serialize(message, descriptor);
    }

    public static TMessage Deserialize<TMessage>(ReadOnlyMemory<byte> body)
        where TMessage : class, IMessage
    {
        var descriptor = MessageBridgeCommandRegistry.GetRequired<TMessage>();
        return (TMessage)descriptor.Deserialize(body.ToArray());
    }

    public static IMessage Deserialize(ReadOnlyMemory<byte> body, MessageBridgeCommandDescriptor descriptor)
    {
        return descriptor.Deserialize(body.ToArray());
    }

    private static MessageBridgeCommandPayload Serialize(IMessage message, MessageBridgeCommandDescriptor descriptor)
    {
        var body = descriptor.Serialize(message);
        return new MessageBridgeCommandPayload(descriptor, body, descriptor.Headers);
    }
}

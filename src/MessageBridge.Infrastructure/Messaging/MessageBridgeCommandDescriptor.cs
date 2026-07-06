using System.Collections.ObjectModel;
using System.Net.Mime;
using Google.Protobuf;

namespace MessageBridge.Infrastructure.Messaging;

public sealed class MessageBridgeCommandDescriptor
{
    private readonly Func<IMessage, byte[]> _serialize;
    private readonly Func<byte[], IMessage> _deserialize;

    private MessageBridgeCommandDescriptor(
        string commandName,
        Type contractType,
        string messageUrn,
        ContentType contentType,
        Func<IMessage, byte[]> serialize,
        Func<byte[], IMessage> deserialize)
    {
        CommandName = commandName;
        ContractType = contractType;
        MessageUrn = messageUrn;
        ContentType = contentType;
        _serialize = serialize;
        _deserialize = deserialize;

        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MessageBridgeHeaders.ContentTypeHeader] = ContentType.MediaType,
                [MessageBridgeHeaders.CommandHeader] = CommandName,
                [MessageBridgeHeaders.MessageUrnHeader] = MessageUrn
            });
    }

    public string CommandName { get; }

    public Type ContractType { get; }

    public string MessageUrn { get; }

    public ContentType ContentType { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public static MessageBridgeCommandDescriptor Create<TMessage>(
        string commandName,
        Func<TMessage, byte[]> serialize,
        Func<byte[], TMessage> deserialize)
        where TMessage : class, IMessage
    {
        var contractType = typeof(TMessage);
        var messageUrn = $"urn:message:{contractType.Namespace}:{contractType.Name}";

        return new MessageBridgeCommandDescriptor(
            commandName,
            contractType,
            messageUrn,
            new ContentType(MessageBridgeHeaders.ProtobufContentType),
            message => serialize((TMessage)message),
            bytes => deserialize(bytes));
    }

    public byte[] Serialize(IMessage message)
    {
        if (!ContractType.IsInstanceOfType(message))
            throw new ArgumentException($"Message must be assignable to {ContractType.FullName}.", nameof(message));

        return _serialize(message);
    }

    public IMessage Deserialize(byte[] body)
    {
        return _deserialize(body);
    }
}

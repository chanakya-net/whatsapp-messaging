namespace MessageBridge.Infrastructure.Messaging;

public static class MessageBridgeHeaders
{
    public const string ContentTypeHeader = "Content-Type";

    public const string CommandHeader = "MessageBridge-Command";

    public const string MessageUrnHeader = "MessageBridge-MessageUrn";

    public const string ProtobufContentType = "application/x-protobuf";
}

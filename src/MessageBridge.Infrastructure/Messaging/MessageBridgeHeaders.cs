namespace MessageBridge.Infrastructure.Messaging;

public static class MessageBridgeHeaders
{
    public const string ContentType = ContentTypeHeader;

    public const string ContentTypeHeader = "Content-Type";

    public const string Encoding = EncodingHeader;

    public const string EncodingHeader = "Content-Encoding";

    public const string CommandName = CommandHeader;

    public const string CommandHeader = "MessageBridge-Command";

    public const string MessageUrn = MessageUrnHeader;

    public const string MessageUrnHeader = "MessageBridge-MessageUrn";

    public const string ProtobufContentType = "application/x-protobuf";

    public const string Utf8Encoding = "utf-8";
}

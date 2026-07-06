using ProtoBuf;

namespace MessageBridge.Publisher.Internal.Contracts;

[ProtoContract]
internal sealed class SendWhatsAppMessageContract
{
    [ProtoMember(1)]
    public string TenantId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string MessageId { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string CorrelationId { get; set; } = string.Empty;

    [ProtoMember(4)]
    public string PhoneNumber { get; set; } = string.Empty;

    [ProtoMember(5)]
    public string TemplateId { get; set; } = string.Empty;

    [ProtoMember(6)]
    public string Body { get; set; } = string.Empty;

    [ProtoMember(7)]
    public string LanguageCode { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class SendEmailConfirmationContract
{
    [ProtoMember(1)]
    public string TenantId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string MessageId { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string CorrelationId { get; set; } = string.Empty;

    [ProtoMember(4)]
    public string Email { get; set; } = string.Empty;

    [ProtoMember(5)]
    public string ConfirmationCode { get; set; } = string.Empty;
}

// Generated protobuf contract - SendWhatsAppMessageCommand

#pragma warning disable 1591

namespace MessageBridge.Contracts.V1;

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

public class SendWhatsAppMessageCommand
{
    private string _messageId = "";
    private string _tenantId = "";
    private string _recipientPhoneNumber = "";
    private string _templateName = "";
    private string _templateLanguage = "";
    private string _correlationId = "";
    private Timestamp _requestedAtUtc;
    private readonly MapField<string, string> _templateParameters = new();

    public string MessageId
    {
        get => _messageId;
        set => _messageId = value ?? "";
    }

    public string TenantId
    {
        get => _tenantId;
        set => _tenantId = value ?? "";
    }

    public string RecipientPhoneNumber
    {
        get => _recipientPhoneNumber;
        set => _recipientPhoneNumber = value ?? "";
    }

    public string TemplateName
    {
        get => _templateName;
        set => _templateName = value ?? "";
    }

    public string TemplateLanguage
    {
        get => _templateLanguage;
        set => _templateLanguage = value ?? "";
    }

    public MapField<string, string> TemplateParameters => _templateParameters;

    public string CorrelationId
    {
        get => _correlationId;
        set => _correlationId = value ?? "";
    }

    public Timestamp RequestedAtUtc
    {
        get => _requestedAtUtc;
        set => _requestedAtUtc = value;
    }

    public SendWhatsAppMessageCommand() { }

    public SendWhatsAppMessageCommand(SendWhatsAppMessageCommand other)
    {
        if (other == null) return;
        _messageId = other._messageId;
        _tenantId = other._tenantId;
        _recipientPhoneNumber = other._recipientPhoneNumber;
        _templateName = other._templateName;
        _templateLanguage = other._templateLanguage;
        _templateParameters.Add(other._templateParameters);
        _correlationId = other._correlationId;
        _requestedAtUtc = other._requestedAtUtc?.Clone();
    }

    public SendWhatsAppMessageCommand Clone() => new(this);

    public byte[] ToByteArray()
    {
        var stream = new System.IO.MemoryStream();
        var output = new CodedOutputStream(stream);
        WriteTo(output);
        output.Flush();
        return stream.ToArray();
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (!string.IsNullOrEmpty(_messageId))
        {
            output.WriteRawTag(10);
            output.WriteString(_messageId);
        }
        if (!string.IsNullOrEmpty(_tenantId))
        {
            output.WriteRawTag(18);
            output.WriteString(_tenantId);
        }
        if (!string.IsNullOrEmpty(_recipientPhoneNumber))
        {
            output.WriteRawTag(26);
            output.WriteString(_recipientPhoneNumber);
        }
        if (!string.IsNullOrEmpty(_templateName))
        {
            output.WriteRawTag(34);
            output.WriteString(_templateName);
        }
        if (!string.IsNullOrEmpty(_templateLanguage))
        {
            output.WriteRawTag(42);
            output.WriteString(_templateLanguage);
        }
        foreach (var pair in _templateParameters)
        {
            var entryStream = new System.IO.MemoryStream();
            var entryOutput = new CodedOutputStream(entryStream);
            entryOutput.WriteTag(1, WireFormat.WireType.LengthDelimited);
            entryOutput.WriteString(pair.Key);
            entryOutput.WriteTag(2, WireFormat.WireType.LengthDelimited);
            entryOutput.WriteString(pair.Value);
            entryOutput.Flush();

            output.WriteTag(6, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(entryStream.ToArray()));
        }
        if (!string.IsNullOrEmpty(_correlationId))
        {
            output.WriteRawTag(58);
            output.WriteString(_correlationId);
        }
        if (_requestedAtUtc != null)
        {
            output.WriteRawTag(66);
            output.WriteMessage(_requestedAtUtc);
        }
    }

    public static SendWhatsAppMessageCommand ParseFrom(byte[] data)
    {
        var input = new CodedInputStream(data);
        var result = new SendWhatsAppMessageCommand();
        result.MergeFrom(input);
        return result;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: _messageId = input.ReadString(); break;
                case 18: _tenantId = input.ReadString(); break;
                case 26: _recipientPhoneNumber = input.ReadString(); break;
                case 34: _templateName = input.ReadString(); break;
                case 42: _templateLanguage = input.ReadString(); break;
                case 50:
                    var mapEntryBytes = input.ReadBytes();
                    var mapEntryInput = new CodedInputStream(mapEntryBytes.ToByteArray());
                    string mapKey = "";
                    string mapValue = "";
                    uint mapTag;
                    while ((mapTag = mapEntryInput.ReadTag()) != 0)
                    {
                        if (mapTag == 10) mapKey = mapEntryInput.ReadString();
                        else if (mapTag == 18) mapValue = mapEntryInput.ReadString();
                        else mapEntryInput.SkipLastField();
                    }
                    _templateParameters[mapKey] = mapValue;
                    break;
                case 58: _correlationId = input.ReadString(); break;
                case 66:
                    if (_requestedAtUtc == null) _requestedAtUtc = new Timestamp();
                    input.ReadMessage(_requestedAtUtc);
                    break;
                default: input.SkipLastField(); break;
            }
        }
    }

    public override bool Equals(object? obj) => Equals(obj as SendWhatsAppMessageCommand);

    public bool Equals(SendWhatsAppMessageCommand? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _messageId == other._messageId &&
               _tenantId == other._tenantId &&
               _recipientPhoneNumber == other._recipientPhoneNumber &&
               _templateName == other._templateName &&
               _templateLanguage == other._templateLanguage &&
               _correlationId == other._correlationId &&
               Equals(_requestedAtUtc, other._requestedAtUtc) &&
               _templateParameters.Equals(other._templateParameters);
    }

    public override int GetHashCode()
    {
        var hash = 1;
        if (!string.IsNullOrEmpty(_messageId)) hash ^= _messageId.GetHashCode();
        if (!string.IsNullOrEmpty(_tenantId)) hash ^= _tenantId.GetHashCode();
        if (!string.IsNullOrEmpty(_recipientPhoneNumber)) hash ^= _recipientPhoneNumber.GetHashCode();
        if (!string.IsNullOrEmpty(_templateName)) hash ^= _templateName.GetHashCode();
        if (!string.IsNullOrEmpty(_templateLanguage)) hash ^= _templateLanguage.GetHashCode();
        if (!string.IsNullOrEmpty(_correlationId)) hash ^= _correlationId.GetHashCode();
        if (_requestedAtUtc != null) hash ^= _requestedAtUtc.GetHashCode();
        hash ^= _templateParameters.GetHashCode();
        return hash;
    }

    public override string ToString() => $"SendWhatsAppMessageCommand {{ MessageId={MessageId}, TenantId={TenantId}, Phone={RecipientPhoneNumber}, Template={TemplateName} }}";
}

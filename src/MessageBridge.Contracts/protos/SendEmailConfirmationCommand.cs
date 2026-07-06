// Generated protobuf contract - SendEmailConfirmationCommand

#pragma warning disable 1591

namespace MessageBridge.Contracts.V1;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public class SendEmailConfirmationCommand
{
    private string _messageId = "";
    private string _tenantId = "";
    private string _recipientEmail = "";
    private string _recipientName = "";
    private string _confirmationToken = "";
    private string _correlationId = "";
    private Timestamp _expiresAtUtc;
    private Timestamp _requestedAtUtc;

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

    public string RecipientEmail
    {
        get => _recipientEmail;
        set => _recipientEmail = value ?? "";
    }

    public string RecipientName
    {
        get => _recipientName;
        set => _recipientName = value ?? "";
    }

    public string ConfirmationToken
    {
        get => _confirmationToken;
        set => _confirmationToken = value ?? "";
    }

    public Timestamp ExpiresAtUtc
    {
        get => _expiresAtUtc;
        set => _expiresAtUtc = value;
    }

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

    public SendEmailConfirmationCommand() { }

    public SendEmailConfirmationCommand(SendEmailConfirmationCommand other)
    {
        if (other == null) return;
        _messageId = other._messageId;
        _tenantId = other._tenantId;
        _recipientEmail = other._recipientEmail;
        _recipientName = other._recipientName;
        _confirmationToken = other._confirmationToken;
        _expiresAtUtc = other._expiresAtUtc?.Clone();
        _correlationId = other._correlationId;
        _requestedAtUtc = other._requestedAtUtc?.Clone();
    }

    public SendEmailConfirmationCommand Clone() => new(this);

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
        if (!string.IsNullOrEmpty(_recipientEmail))
        {
            output.WriteRawTag(26);
            output.WriteString(_recipientEmail);
        }
        if (!string.IsNullOrEmpty(_recipientName))
        {
            output.WriteRawTag(34);
            output.WriteString(_recipientName);
        }
        if (!string.IsNullOrEmpty(_confirmationToken))
        {
            output.WriteRawTag(42);
            output.WriteString(_confirmationToken);
        }
        if (_expiresAtUtc != null)
        {
            output.WriteRawTag(50);
            output.WriteMessage(_expiresAtUtc);
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

    public static SendEmailConfirmationCommand ParseFrom(byte[] data)
    {
        var input = new CodedInputStream(data);
        var result = new SendEmailConfirmationCommand();
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
                case 26: _recipientEmail = input.ReadString(); break;
                case 34: _recipientName = input.ReadString(); break;
                case 42: _confirmationToken = input.ReadString(); break;
                case 50:
                    if (_expiresAtUtc == null) _expiresAtUtc = new Timestamp();
                    input.ReadMessage(_expiresAtUtc);
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

    public override bool Equals(object? obj) => Equals(obj as SendEmailConfirmationCommand);

    public bool Equals(SendEmailConfirmationCommand? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _messageId == other._messageId &&
               _tenantId == other._tenantId &&
               _recipientEmail == other._recipientEmail &&
               _recipientName == other._recipientName &&
               _confirmationToken == other._confirmationToken &&
               _correlationId == other._correlationId &&
               Equals(_expiresAtUtc, other._expiresAtUtc) &&
               Equals(_requestedAtUtc, other._requestedAtUtc);
    }

    public override int GetHashCode()
    {
        var hash = 1;
        if (!string.IsNullOrEmpty(_messageId)) hash ^= _messageId.GetHashCode();
        if (!string.IsNullOrEmpty(_tenantId)) hash ^= _tenantId.GetHashCode();
        if (!string.IsNullOrEmpty(_recipientEmail)) hash ^= _recipientEmail.GetHashCode();
        if (!string.IsNullOrEmpty(_recipientName)) hash ^= _recipientName.GetHashCode();
        if (!string.IsNullOrEmpty(_confirmationToken)) hash ^= _confirmationToken.GetHashCode();
        if (!string.IsNullOrEmpty(_correlationId)) hash ^= _correlationId.GetHashCode();
        if (_expiresAtUtc != null) hash ^= _expiresAtUtc.GetHashCode();
        if (_requestedAtUtc != null) hash ^= _requestedAtUtc.GetHashCode();
        return hash;
    }

    public override string ToString() => $"SendEmailConfirmationCommand {{ MessageId={MessageId}, TenantId={TenantId}, Email={RecipientEmail} }}";
}

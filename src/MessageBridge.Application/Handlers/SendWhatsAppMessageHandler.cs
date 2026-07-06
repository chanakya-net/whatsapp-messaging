using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;

namespace MessageBridge.Application.Handlers;

public sealed class SendWhatsAppMessageHandler
{
    private readonly IWhatsAppMessageSender _sender;
    private readonly IMessageProcessingStore _store;

    public SendWhatsAppMessageHandler(IWhatsAppMessageSender sender, IMessageProcessingStore store)
    {
        _sender = sender;
        _store = store;
    }

    public async Task<ErrorOr<Success>> Handle(SendWhatsAppMessage command)
    {
        var message = new WhatsAppMessage(
            MessageId: command.MessageId,
            RecipientPhoneNumber: command.RecipientPhoneNumber,
            TemplateName: command.TemplateName,
            TemplateLanguage: command.TemplateLanguage,
            TemplateParameters: command.TemplateParameters,
            CorrelationId: command.CorrelationId,
            RequestedAtUtc: command.RequestedAtUtc);

        var sendResult = await _sender.SendAsync(message, command.TenantId);
        if (sendResult.IsError)
            return sendResult.Errors;

        var storeResult = await _store.RecordMessageSentAsync(command.MessageId, command.TenantId);
        if (storeResult.IsError)
            return storeResult.Errors;

        return new Success();
    }
}

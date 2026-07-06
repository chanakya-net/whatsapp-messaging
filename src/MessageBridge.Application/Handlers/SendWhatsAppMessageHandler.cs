using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;

namespace MessageBridge.Application.Handlers;

public sealed class SendWhatsAppMessageHandler
{
    private readonly IWhatsAppMessageSender _sender;
    private readonly IMessageProcessingStore _store;
    private readonly ITenantConfigurationProvider _tenantConfigProvider;
    private readonly IProviderRateLimiter _rateLimiter;

    public SendWhatsAppMessageHandler(
        IWhatsAppMessageSender sender,
        IMessageProcessingStore store,
        ITenantConfigurationProvider tenantConfigProvider,
        IProviderRateLimiter rateLimiter)
    {
        _sender = sender;
        _store = store;
        _tenantConfigProvider = tenantConfigProvider;
        _rateLimiter = rateLimiter;
    }

    public async Task<ErrorOr<Success>> Handle(SendWhatsAppMessage command)
    {
        var tenantConfigResult = await _tenantConfigProvider.GetTenantConfigAsync(command.TenantId);
        if (tenantConfigResult.IsError)
            return tenantConfigResult.Errors;

        var rateLimitResult = await _rateLimiter.CheckRateLimitAsync(command.TenantId, "whatsapp");
        if (rateLimitResult.IsError)
            return rateLimitResult.Errors;

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

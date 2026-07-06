using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;

namespace MessageBridge.Application.Handlers;

public sealed class SendEmailConfirmationHandler
{
    private readonly IEmailConfirmationSender _sender;
    private readonly IMessageProcessingStore _store;

    public SendEmailConfirmationHandler(IEmailConfirmationSender sender, IMessageProcessingStore store)
    {
        _sender = sender;
        _store = store;
    }

    public async Task<ErrorOr<Success>> Handle(SendEmailConfirmation command)
    {
        var email = new EmailConfirmation(
            MessageId: command.MessageId,
            RecipientEmailAddress: command.RecipientEmail,
            TemplateName: $"confirm-email",
            ConfirmationToken: command.ConfirmationToken,
            CorrelationId: command.CorrelationId,
            RequestedAtUtc: command.RequestedAtUtc);

        var sendResult = await _sender.SendAsync(email, command.TenantId);
        if (sendResult.IsError)
            return sendResult.Errors;

        var storeResult = await _store.RecordMessageSentAsync(command.MessageId, command.TenantId);
        if (storeResult.IsError)
            return storeResult.Errors;

        return new Success();
    }
}

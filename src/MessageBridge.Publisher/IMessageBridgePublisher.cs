using System.Threading;
using ErrorOr;
using MessageBridge.Publisher.Requests;

namespace MessageBridge.Publisher;

public interface IMessageBridgePublisher
{
    Task<ErrorOr<MessageBridgePublisherResult>> PublishWhatsAppMessageAsync(
        SendWhatsAppMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<ErrorOr<MessageBridgePublisherResult>> PublishEmailConfirmationAsync(
        SendEmailConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

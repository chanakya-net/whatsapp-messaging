using FluentValidation;
using MessageBridge.Publisher.Internal;
using MessageBridge.Publisher.Validation;
using MessageBridge.Publisher.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher;

public static class DependencyInjection
{
    public static IServiceCollection AddMessageBridgePublisher(
        this IServiceCollection services,
        Action<MessageBridgePublisherOptions> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions<MessageBridgePublisherOptions>()
            .Configure(configure)
            .ValidateDataAnnotations();

        services.AddSingleton<IValidateOptions<MessageBridgePublisherOptions>, MessageBridgePublisherOptionsValidator>();
        services.AddSingleton<IValidator<SendWhatsAppMessageRequest>, SendWhatsAppMessageRequestValidator>();
        services.AddSingleton<IValidator<SendEmailConfirmationRequest>, SendEmailConfirmationRequestValidator>();
        services.AddSingleton<IMessageBridgePublisherTransport, MassTransitMessageBridgeTransport>();
        services.AddSingleton<IMessageBridgePublisher, DirectMessageBridgePublisher>();

        return services;
    }
}

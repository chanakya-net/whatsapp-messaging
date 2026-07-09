using MessageBridge.Application.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBridge.Worker;

internal static class WorkerRuntimeDependencyValidation
{
    public static void ValidateWorkerRuntimeDependencies(this IServiceProvider services)
    {
        ValidateHandler<SendWhatsAppMessageHandler>(services);
        ValidateHandler<SendEmailConfirmationHandler>(services);
    }

    private static void ValidateHandler<THandler>(IServiceProvider services)
        where THandler : notnull =>
        ActivatorUtilities.CreateInstance<THandler>(services);
}

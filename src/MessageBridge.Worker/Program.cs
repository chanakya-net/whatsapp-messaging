using MessageBridge.Application.Handlers;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Worker;
using MessageBridge.Worker.Observability;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMessageBridgeMassTransit(builder.Configuration);
builder.Services.AddMessageBridgeObservability(builder.Configuration);
builder.Services.AddWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(SendWhatsAppMessageHandler).Assembly);
});

var observabilityOptions = new ObservabilityOptions();
builder.Configuration.GetSection(ObservabilityOptions.SectionName).Bind(observabilityOptions);
builder.Logging.AddMessageBridgeOpenTelemetryLogging(observabilityOptions);

var app = builder.Build();
app.Services.ValidateWorkerRuntimeDependencies();

app.MapMessageBridgeHealthAndMetrics();

app.Run();

public partial class Program;

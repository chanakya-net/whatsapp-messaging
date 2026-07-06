using MessageBridge.Application.Handlers;
using MessageBridge.Infrastructure.Messaging;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddMessageBridgeMassTransit(builder.Configuration);
builder.Services.AddWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(SendWhatsAppMessageHandler).Assembly);
});

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();

public partial class Program;

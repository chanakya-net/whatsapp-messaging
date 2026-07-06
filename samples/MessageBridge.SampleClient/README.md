# MessageBridge Sample Client

Demonstrates both direct and outbox-based publishing patterns for MessageBridge.

## Direct Publisher

Publishes messages directly to the transport (MassTransit/AMQP):

```csharp
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "sample-tenant";
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "whatsapp.send";
    opts.EmailRoutingKey = "email.confirmation";
});

var publisher = serviceProvider.GetRequiredService<IMessageBridgePublisher>();

var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    TenantId = "sample-tenant",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Welcome to MessageBridge!",
});
```

## Outbox Publisher

Stores messages in a database outbox table before dispatching. Ensures transactional consistency:

```csharp
services.AddDbContext<SampleDbContext>(opts => 
    opts.UseInMemoryDatabase("samples"));
services.AddMessageBridgeOutboxPublisher<SampleDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.PollIntervalMilliseconds = 5000;
    opts.CleanupEnabled = true;
});
```

Messages are written to the outbox during the same transaction as application state changes, then asynchronously dispatched.

## Build

```bash
dotnet build samples/MessageBridge.SampleClient/MessageBridge.SampleClient.csproj
```

## Run

```bash
dotnet run --project samples/MessageBridge.SampleClient/MessageBridge.SampleClient.csproj
```

Configuration uses placeholders only — no real CloudAMQP credentials required.

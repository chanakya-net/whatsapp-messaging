# MessageBridge Publisher Guide

`MessageBridge.Publisher` provides a typed API for publishing WhatsApp and email confirmation commands.

## API Surface

`IMessageBridgePublisher` exposes:

- `Task<ErrorOr<MessageBridgePublisherResult>> PublishWhatsAppMessageAsync(SendWhatsAppMessageRequest request, CancellationToken cancellationToken = default)`
- `Task<ErrorOr<MessageBridgePublisherResult>> PublishEmailConfirmationAsync(SendEmailConfirmationRequest request, CancellationToken cancellationToken = default)`

The result type is `MessageBridgePublisherResult`, which includes the resolved `MessageId`, `CorrelationId`, and `TenantId`.

## Direct Mode

Use direct mode when you want a publish call to go straight to RabbitMQ.

```csharp
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "tenant-1";
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "whatsapp.send";
    opts.EmailRoutingKey = "email.confirmation";
});

var publisher = serviceProvider.GetRequiredService<IMessageBridgePublisher>();

var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    TenantId = "tenant-1",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Hello!",
    LanguageCode = "en-US"
});
```

Direct mode behavior:

- Validates the request before publish
- Generates `MessageId` when omitted
- Generates `CorrelationId` from trace context or ULID when omitted
- Returns `ErrorOr<MessageBridgePublisherResult>`

## Outbox Mode

Use outbox mode when you need the publish request to be stored with your database transaction and dispatched later by hosted services.

```csharp
services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connectionString));
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.Concurrency = 4;
    opts.PollIntervalMilliseconds = 500;
    opts.MaxRetryAttempts = 3;
    opts.RetryDelayMilliseconds = 50;
    opts.RetryBackoffMultiplier = 2.0;
    opts.CleanupEnabled = true;
    opts.CleanupRetentionHours = 24;
    opts.CleanupBatchSize = 500;
});
```

The package also exposes `AddMessageBridgeOutboxDispatcher<TContext>` and `AddMessageBridgeOutboxCleanup<TContext>` if you want those hosted services separately.

Outbox configuration options:

- `BatchSize` - messages read per dispatch batch
- `Concurrency` - parallel publishes per batch
- `PollIntervalMilliseconds` - dispatcher polling interval
- `MaxRetryAttempts` - retry attempts per message publish cycle
- `RetryDelayMilliseconds` - delay before the first retry
- `RetryBackoffMultiplier` - multiplier applied to each retry delay
- `CleanupEnabled` - enables cleanup hosted service
- `CleanupRetentionHours` - keep published rows for this long
- `CleanupBatchSize` - rows removed per cleanup pass
- `CleanupIntervalMilliseconds` - cleanup polling interval

## Request DTOs

### WhatsApp Request DTO

`SendWhatsAppMessageRequest` includes:

- `TenantId`
- `PhoneNumber`
- `TemplateId`
- `Body`
- `LanguageCode`
- `MessageId`
- `CorrelationId`

### Email Request DTO

`SendEmailConfirmationRequest` includes:

- `TenantId`
- `Email`
- `ConfirmationCode`
- `MessageId`
- `CorrelationId`

## Tenant Behavior

Tenant handling matches the current options and validators:

- `DefaultTenantId` is required during registration
- If a request omits `TenantId`, the default tenant is used
- `AllowedTenantIds` can restrict publishing to an allow-list

```csharp
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "dev-tenant";
    opts.AllowedTenantIds = ["dev-tenant", "qa-tenant"];
});
```

## Sample Usage

### WhatsApp

```csharp
var whatsappResult = await publisher.PublishWhatsAppMessageAsync(
    new SendWhatsAppMessageRequest
    {
        TenantId = "acme-corp",
        PhoneNumber = "+1-555-0100",
        TemplateId = "order-confirmation",
        Body = "Your order #12345 is confirmed.",
        LanguageCode = "en-US",
        CorrelationId = "order-12345"
    });

if (whatsappResult.IsError)
{
    logger.LogWarning("Publish failed: {Errors}", whatsappResult.Errors);
    return;
}

logger.LogInformation("Published message {MessageId}", whatsappResult.Value.MessageId);
```

### Email

```csharp
var emailResult = await publisher.PublishEmailConfirmationAsync(
    new SendEmailConfirmationRequest
    {
        TenantId = "acme-corp",
        Email = "user@example.com",
        ConfirmationCode = confirmationCode,
        CorrelationId = "signup-12345"
    });
```

## Error Handling

The publisher returns `ErrorOr<MessageBridgePublisherResult>`.

- Validation failures are returned as `Error.Validation(...)`
- Transport failures in direct mode bubble up from the transport implementation
- Callers should inspect `IsError` before using `Value`

## Security Notes

- Never log full message bodies or confirmation codes
- Never put secrets in request DTOs
- Treat tenant IDs and correlation IDs as metadata, not credentials
- Use environment variables or a secrets provider for connection strings and broker credentials

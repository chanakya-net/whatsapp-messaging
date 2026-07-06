# MessageBridge Publisher Guide

The Publisher package provides a typed, transport-agnostic API for publishing WhatsApp and email confirmation commands.

## Overview

`MessageBridge.Publisher` exposes two main methods via `IMessageBridgePublisher`:

- `PublishWhatsAppMessageAsync(SendWhatsAppMessageRequest request, CancellationToken cancellationToken = default)`
- `PublishEmailConfirmationAsync(SendEmailConfirmationRequest request, CancellationToken cancellationToken = default)`

The Publisher handles:

- Request validation
- Message ID generation (ULID by default)
- Correlation ID propagation or generation
- Tenant ID defaulting and validation
- Protobuf serialization
- Transport routing (exchange, queues, routing keys)

## Direct Mode

**Use for:** immediate publish with lowest latency. Best for non-critical notifications.

```csharp
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "tenant-1";  // Optional; required if RequireTenantId = true
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "send-whatsapp-message.v1";
    opts.EmailRoutingKey = "send-email-confirmation.v1";
});

var publisher = serviceProvider.GetRequiredService<IMessageBridgePublisher>();

await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    TenantId = "tenant-1",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Hello!"
});
```

**Behavior:**

- Publishes synchronously to RabbitMQ
- Returns after successful broker acknowledgment
- No retry on transient failures (caller must retry if needed)
- Idempotency relies on worker-side duplicate detection

## Outbox Mode

**Use for:** guaranteed at-least-once delivery within a database transaction. Recommended for production.

```csharp
services.AddDbContext<AppDbContext>(opts => opts.UsePostgresql(...));
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.PollIntervalMilliseconds = 5000;
});

// In your service:
using (var transaction = await dbContext.Database.BeginTransactionAsync())
{
    // Insert your business data
    dbContext.Orders.Add(myOrder);
    
    // Record outbox message atomically
    await publisher.PublishWhatsAppMessageAsync(
        new SendWhatsAppMessageRequest { /* ... */ }
    );
    
    await dbContext.SaveChangesAsync();
    await transaction.CommitAsync();
}
```

**Behavior:**

- Stores pending message in client application's outbox table
- Message recorded atomically with your business transaction
- Dispatcher runs as a hosted service in your app
- Dispatcher publishes pending messages in batches
- Records marked published only after broker acknowledgment
- Failed publishes retained for inspection
- **At-least-once guarantee:** duplicates are possible; worker idempotency handles them

**Configuration:**

- `BatchSize` — messages per publish batch (default: 100)
- `PollIntervalMilliseconds` — check for pending messages (default: 5000)
- `MaxRetries` — attempts per message (default: 5)
- `RetentionDays` — keep published records (default: 7)

## Message IDs

IDs uniquely identify messages for idempotency.

- Publisher generates ULID if not supplied
- Caller may provide custom string (max 128 characters)
- Format is opaque; system uses `message_id + message_type` as deduplication key

```csharp
var request = new SendWhatsAppMessageRequest
{
    MessageId = "custom-id-123",  // Optional; omit to auto-generate
    /* ... */
};
```

## Correlation IDs

Link messages to requests or user actions for tracing.

- Publisher prefers W3C trace context (OpenTelemetry) if available
- Fallback: generated ULID-like string
- Caller may provide custom string (max 128 characters)

```csharp
var request = new SendWhatsAppMessageRequest
{
    CorrelationId = "req-abc123",  // Optional; omit for auto-generation
    /* ... */
};
```

## Tenant Behavior

Multi-tenant support via required `TenantId`.

**Modes:**

1. **Explicit per-request** — always pass `TenantId` on the request
2. **Configured default** — set `DefaultTenantId` during registration (dev/test convenience)
3. **Required explicit** — set `RequireTenantId = true` to forbid publishing without explicit tenant

```csharp
// Dev: allow default tenant
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "dev-tenant";
    opts.RequireTenantId = false;  // Default
});

// Production: require explicit tenant
services.AddMessageBridgePublisher(opts =>
{
    opts.RequireTenantId = true;  // Reject if request.TenantId is null
});
```

## Sample Usage

### WhatsApp Message

```csharp
await publisher.PublishWhatsAppMessageAsync(
    new SendWhatsAppMessageRequest
    {
        TenantId = "acme-corp",
        PhoneNumber = "+1-555-0100",
        TemplateId = "order-confirmation",
        Body = "Your order #12345 is confirmed.",
        CorrelationId = "order-12345"  // Links to your business transaction
    }
);
```

### Email Confirmation

```csharp
var confirmationToken = tokenService.GenerateToken();

await publisher.PublishEmailConfirmationAsync(
    new SendEmailConfirmationRequest
    {
        TenantId = "acme-corp",
        RecipientEmail = "user@example.com",
        RecipientName = "John Doe",
        ConfirmationToken = confirmationToken,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
    }
);
```

## Error Handling

Publisher returns `ErrorOr<Success>`. Validation errors are surfaced immediately:

```csharp
var result = await publisher.PublishWhatsAppMessageAsync(request);

if (result.IsError)
{
    foreach (var error in result.Errors)
    {
        logger.LogWarning("Publish failed: {Error}", error.Description);
    }
}
```

Expected errors (invalid input, validation failure) fail fast.
Transient broker errors (in direct mode) propagate as exceptions; wrap in try/catch if needed.

## Security Notes

- Never log full message bodies or sensitive template parameters
- Phone numbers and emails are masked in logs: `+1***0100`, `u***@example.com`
- Confirmation tokens and secrets are never logged
- Connection strings and credentials use environment variables or secrets providers
- No credentials are embedded in request DTOs

# Operations Guide

This guide covers runtime health checks, retries, the outbox table, failure handling, cleanup, and observability.

## Health Checks

The worker exposes:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

- `/health/live` returns a simple liveness response
- `/health/ready` checks RabbitMQ and PostgreSQL readiness

## Retry Policy

Outbox publishing is retried inside the dispatcher using the actual `MessageBridgeOutboxOptions` values:

- `MaxRetryAttempts`
- `RetryDelayMilliseconds`
- `RetryBackoffMultiplier`
- `PollIntervalMilliseconds`
- `BatchSize`
- `Concurrency`

Example:

```csharp
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.Concurrency = 4;
    opts.PollIntervalMilliseconds = 500;
    opts.MaxRetryAttempts = 3;
    opts.RetryDelayMilliseconds = 50;
    opts.RetryBackoffMultiplier = 2.0;
});
```

If a message still fails after the configured retry attempts, it remains unpublished and will be picked up again on the next poll cycle. There is no `Status` column in the outbox table.

## Rate Limiting

The worker enforces per-tenant, per-channel rate limits on email and WhatsApp message delivery.

Configuration via `MessageBridge:RateLimiting`:

| Setting | Default | Description |
|---------|---------|---|
| `WhatsAppPermitsPerWindow` | `60` | Maximum WhatsApp messages per window |
| `EmailPermitsPerWindow` | `60` | Maximum email messages per window |
| `WindowSizeSeconds` | `60` | Rate limit window duration in seconds |

Example:

```json
{
  "MessageBridge": {
    "RateLimiting": {
      "WhatsAppPermitsPerWindow": 60,
      "EmailPermitsPerWindow": 60,
      "WindowSizeSeconds": 60
    }
  }
}
```

**Replicas and Correctness:** The in-memory rate limiter maintains state per replica. Correctness requires **single-replica deployment**: set Container Apps `min_replicas = max_replicas = 1`. Multi-replica deployments will not coordinate rate limits across instances and will exceed the per-tenant, per-channel ceiling. Exceeding the limit returns a transient error; clients should retry with backoff.

## Outbox Table

The outbox entity is mapped to `MessageBridgeOutboxMessages` with these columns:

- `Id`
- `MessageId`
- `CorrelationId`
- `ExchangeName`
- `RoutingKey`
- `Headers`
- `Payload`
- `CreatedAtUtc`
- `PublishedAtUtc`

`PublishedAtUtc IS NULL` means the message is still pending.

### Useful Queries

```sql
-- Pending messages
SELECT COUNT(*)
FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NULL;

-- Old pending messages
SELECT Id, MessageId, CorrelationId, ExchangeName, RoutingKey, CreatedAtUtc
FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NULL
  AND CreatedAtUtc < NOW() - INTERVAL '15 minutes'
ORDER BY CreatedAtUtc;

-- Recently published messages
SELECT Id, MessageId, CorrelationId, ExchangeName, RoutingKey, CreatedAtUtc, PublishedAtUtc
FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NOT NULL
ORDER BY PublishedAtUtc DESC
LIMIT 20;
```

## Error Handling

MessageBridge uses MassTransit fault consumers for dispatched contract faults.

- The fault consumers record failed processing in the application store
- Broker dead-letter or error queues depend on the RabbitMQ/MassTransit deployment configuration
- The repository does not hardcode a `messagebridge.errors` queue name

Use broker tooling to inspect any dead-letter queues configured by your environment.

## Cleanup and Retention

### Outbox Cleanup

Outbox cleanup is controlled by these options:

- `CleanupEnabled`
- `CleanupRetentionHours`
- `CleanupBatchSize`
- `CleanupIntervalMilliseconds`

Example:

```csharp
services.AddMessageBridgeOutboxCleanup<AppDbContext>(opts =>
{
    opts.CleanupEnabled = true;
    opts.CleanupRetentionHours = 24;
    opts.CleanupBatchSize = 500;
    opts.CleanupIntervalMilliseconds = 1000;
});
```

Cleanup removes rows whose `PublishedAtUtc` value is older than the configured retention window.

#### Manual Cleanup

```sql
DELETE FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NOT NULL
  AND PublishedAtUtc < NOW() - INTERVAL '24 hours';
```

### Processing History Retention

Processing history cleanup is environment-aware and preserves failed/rejected records indefinitely.

> **Migration note:** the `CleanupRetentionHours` setting under `MessageBridge:ProcessingHistory` has been removed. It is superseded by `DevelopmentRetentionHours` and `ProductionRetentionHours`. Any existing `MessageBridge__ProcessingHistory__CleanupRetentionHours` environment variable or config key is now ignored; set the environment-specific values below instead.

Configuration via `MessageBridge:ProcessingHistory`:

| Setting | Default (Dev) | Default (Prod) | Description |
|---------|---|---|---|
| `CleanupEnabled` | `false` | `false` | Enable/disable processing history cleanup |
| `DevelopmentRetentionHours` | `24` | `24` | Completed/Abandoned retention in development (≥1, ≤3650) |
| `ProductionRetentionHours` | `168` | `168` | Completed/Abandoned retention in production, 7 days (≥1, ≤3650) |
| `CleanupBatchSize` | `500` | `500` | Records per cleanup run (≥1, ≤10,000) |
| `CleanupIntervalMilliseconds` | `1000` | `1000` | Cleanup run interval in ms (≥1, ≤3,600,000) |

Behavior:

- **Development** environment: removes Completed/Abandoned records older than 24 hours
- **Production** environment: removes Completed/Abandoned records older than 168 hours (7 days)
- **Failed** and **Rejected** records: preserved indefinitely (never removed)
- **Processing/Received/other statuses**: preserved indefinitely

Example configuration:

```json
{
  "MessageBridge": {
    "ProcessingHistory": {
      "CleanupEnabled": true,
      "DevelopmentRetentionHours": 24,
      "ProductionRetentionHours": 168,
      "CleanupBatchSize": 500,
      "CleanupIntervalMilliseconds": 1000
    }
  }
}
```

Enable via environment variables:

```bash
# Development
export MessageBridge__ProcessingHistory__CleanupEnabled=true
export MessageBridge__ProcessingHistory__DevelopmentRetentionHours=24

# Production
export MessageBridge__ProcessingHistory__CleanupEnabled=true
export MessageBridge__ProcessingHistory__ProductionRetentionHours=168
```

Verify cleanup behavior:

```sql
-- Development: inspect records older than the 24-hour cutoff.
SELECT Status, COUNT(*) as Count
FROM MessageProcessingRecords
WHERE ProcessedAt < NOW() - INTERVAL '24 hours'
GROUP BY Status;

-- Expect: Completed/Abandoned older than 24 hours absent; Failed/Rejected and
-- Received/Processing records remain.

-- Production: inspect records older than the 168-hour (7-day) cutoff.
SELECT Status, COUNT(*) as Count
FROM MessageProcessingRecords
WHERE ProcessedAt < NOW() - INTERVAL '168 hours'
GROUP BY Status;

-- Expect: Completed/Abandoned older than 168 hours absent; Failed/Rejected and
-- Received/Processing records remain. Records aged 24-168 hours are retained.
```

## Idempotency

Operators should expect duplicate delivery under retries and restarts.

- `MessageId` is the deduplication key
- `CorrelationId` groups related messages
- Consumers should store processed message IDs where side effects matter

## Observability

The worker exposes structured logs, OpenTelemetry traces, and optional Prometheus metrics.

### Provider delivery status

The WhatsApp and email adapters currently acknowledge requests as `simulated`; they do not contact a provider. Treat `delivery_status=simulated` in logs and metadata as an explicit simulation marker, not proof of delivery. The provider message ID is synthetic, and recipient or credential data must not be added to logs or metadata.

`MessageBridge:Providers:WhatsAppProviderName` and `MessageBridge:Providers:EmailProviderName` are reserved diagnostic labels only. Values supplied through Azure Key Vault or another secrets provider remain inactive placeholders and do not enable delivery. Real provider adapters are out of scope.

```csharp
var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    TenantId = "acme-corp",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Welcome!",
    LanguageCode = "en-US"
});
```

Suggested alert targets:

- `/health/ready` returning non-200
- `MessageBridgeOutboxMessages` with growing `PublishedAtUtc IS NULL` counts
- repeated publish failures in application logs

## Incident Response

1. Check `/health/ready`
2. Inspect RabbitMQ connectivity
3. Inspect PostgreSQL connectivity
4. Query `MessageBridgeOutboxMessages` for pending rows
5. Review application logs for publish failures

## See Also

- [Local Development](local-development.md)
- [Deployment](deployment.md)
- [Message Contracts](contracts.md)
- [Publisher Guide](publisher.md)

# Operations Guide

Running MessageBridge in production: health checks, retries, error handling, idempotency, cleanup, and observability.

## Health Checks

The Worker Service exposes health check endpoints via ASP.NET Core:

```bash
curl http://localhost:8080/health
curl http://localhost:8080/health/ready
curl http://localhost:8080/health/live
```

Health checks verify:
- **RabbitMQ connectivity** — can connect to message broker
- **PostgreSQL connectivity** — can connect to database (outbox mode)
- **Outbox processing** — (if enabled) dispatcher is running and processing messages

### Kubernetes Probes

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 30
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 5
```

## Message Retry Policy

Messages that fail to publish are retried automatically using exponential backoff.

### Default Retry Behavior

- **Max retries**: 3 attempts
- **Backoff**: exponential (1s, 2s, 4s)
- **Timeout per attempt**: 5 seconds

### Configuration (Outbox Mode)

```csharp
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.MaxRetries = 3;              // Total retry attempts
    opts.InitialBackoffMilliseconds = 1000;
    opts.BackoffMultiplier = 2.0;
    opts.CommandTimeoutSeconds = 5;
});
```

### Behavior on Retry Exhaustion

After max retries exceeded:
1. Message is **not deleted** from outbox (persisted for inspection)
2. Message is **marked as failed** (status = `Failed`)
3. **Alert** is logged (structured log entry with message ID, reason, tenant ID)
4. **Manual intervention** required (see Error Queues section)

## Error Queues

Messages that fail to deliver are moved to an error queue in RabbitMQ:

- **Queue name**: `messagebridge.errors`
- **Messages in error queue** contain:
  - Original message payload (protobuf binary)
  - Failure reason (connection timeout, validation error, etc.)
  - Timestamp & attempt count
  - Tenant ID & message ID for correlation

### Recovering from Error Queue

1. **Inspect** failed message via RabbitMQ management UI:
   ```
   http://localhost:15672 → Queues → messagebridge.errors
   ```

2. **Diagnose** the failure:
   - Network/connectivity issues → fix upstream service, retry
   - Validation error → fix sender application, reformat message, republish
   - Throttling → backoff, batch smaller, retry

3. **Requeue** manually:
   ```bash
   # Example: move message back to whatsapp queue for retry
   # (Implementation varies by RabbitMQ client; see RabbitMQ CLI tools)
   ```

## Idempotency

All messages include unique identifiers to enable safe retries:

- **MessageId** — globally unique per message (UUIDv7, generated automatically)
- **CorrelationId** — traces related messages across the system (optional, provided by caller)
- **TenantId** — required on every publish call

Workers and downstream services **must deduplicate by MessageId** to handle duplicate deliveries gracefully:

```csharp
// Pseudocode: worker handler
public async Task Handle(SendWhatsAppMessageCommand cmd)
{
    // Check if already processed
    var existing = await _db.ProcessedMessages
        .FirstOrDefaultAsync(m => m.MessageId == cmd.MessageId);
    
    if (existing != null)
        return;  // Already handled, skip
    
    // Process message
    await SendWhatsAppAsync(cmd.PhoneNumber, cmd.TemplateId, cmd.Body);
    
    // Record as processed
    await _db.ProcessedMessages.AddAsync(new ProcessedMessage 
    { 
        MessageId = cmd.MessageId,
        ProcessedAt = DateTime.UtcNow,
        TenantId = cmd.TenantId
    });
    await _db.SaveChangesAsync();
}
```

## Outbox Processing

The outbox pattern guarantees "at-least-once" delivery:

1. **Write phase** — application writes message + business state in a single transaction
2. **Dispatch phase** — background worker polls outbox, publishes to RabbitMQ, marks as sent
3. **Cleanup phase** — old sent messages are deleted (configurable retention)

### Outbox Polling

Dispatcher runs every 5 seconds (configurable) and:
- Queries unsent messages from `MessageBridgeOutbox` table
- Publishes to RabbitMQ in batches (default: 100 messages/batch)
- Updates status to `Sent` on success
- Retries on failure (up to max retries)
- Logs failures for alerting

### Configuration

```csharp
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.PollIntervalMilliseconds = 5000;    // Check every 5s
    opts.BatchSize = 100;                    // Publish 100 at a time
    opts.CleanupEnabled = true;              // Delete old sent messages
    opts.RetentionDays = 7;                  // Keep for 7 days, then delete
});
```

### Monitoring Outbox

```sql
-- Check pending messages
SELECT COUNT(*) 
FROM MessageBridgeOutbox 
WHERE Status = 'Pending'
  AND TenantId = 'my-tenant';

-- Check failed messages
SELECT MessageId, FailureReason, AttemptCount, CreatedAt
FROM MessageBridgeOutbox 
WHERE Status = 'Failed'
ORDER BY CreatedAt DESC
LIMIT 10;

-- Check old sent messages (candidates for cleanup)
SELECT COUNT(*), MIN(SentAt), MAX(SentAt)
FROM MessageBridgeOutbox 
WHERE Status = 'Sent'
  AND SentAt < NOW() - INTERVAL '7 days';
```

## Cleanup & Retention

Old sent messages consume disk space and should be cleaned up regularly.

### Automatic Cleanup

If `CleanupEnabled = true`:
- Runs after every dispatch cycle
- Deletes messages with `Status = Sent` older than `RetentionDays`
- Logs count deleted

### Manual Cleanup

```sql
-- Delete sent messages older than 30 days
DELETE FROM MessageBridgeOutbox
WHERE Status = 'Sent'
  AND SentAt < NOW() - INTERVAL '30 days';
```

### Backup Before Cleanup

Before large deletions, archive to cold storage:

```bash
# Export failed/pending messages for investigation
psql messagebridge_prod -c "
  COPY (
    SELECT * FROM MessageBridgeOutbox 
    WHERE Status IN ('Failed', 'Pending')
  ) TO STDOUT WITH CSV HEADER" > outbox_failures_$(date +%Y%m%d).csv

# Then delete old sent messages
psql messagebridge_prod -c "
  DELETE FROM MessageBridgeOutbox
  WHERE Status = 'Sent' AND SentAt < NOW() - INTERVAL '30 days';"
```

## Observability

### Structured Logging

MessageBridge uses structured logging with serilog. Key log levels:

- **Info** — message published, dispatcher cycle complete
- **Warning** — retry attempted, temporary failure
- **Error** — exhausted retries, validation failed, critical error

Example log entry (JSON):
```json
{
  "timestamp": "2024-01-15T10:30:45.123Z",
  "level": "Error",
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "tenantId": "acme-corp",
  "messageType": "SendWhatsAppMessage",
  "exception": "RabbitMQ connection timeout after 5s",
  "attemptCount": 3,
  "message": "Failed to publish message after max retries"
}
```

### Metrics (if using OpenTelemetry)

- `messagebridge.publish.total` — total messages published (counter)
- `messagebridge.publish.duration_ms` — time to publish (histogram)
- `messagebridge.outbox.pending` — messages awaiting dispatch (gauge)
- `messagebridge.outbox.failed` — messages in failed state (gauge)
- `messagebridge.retry.count` — retries by failure reason (counter)

Configure exporters in `appsettings.Production.json`:

```json
{
  "OpenTelemetry": {
    "Exporters": {
      "Jaeger": {
        "Endpoint": "http://jaeger-collector:4418/v1/traces"
      },
      "PrometheusMetrics": {
        "Endpoint": "http://prometheus:9090"
      }
    }
  }
}
```

### Distributed Tracing

All published messages include a `CorrelationId` (or one is generated):

```csharp
// Trace a message across services
var result = await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    CorrelationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
});
```

Trace queries in Jaeger / Tempo:
```
service.name="MessageBridge.Worker" AND correlation_id="550e8400-e29b-41d4..."
```

## Alerting

Configure alerts on:

- **Outbox queue growing** — dispatcher falling behind
- **Error queue not empty** — messages failing to deliver
- **Health check failing** — loss of RabbitMQ/PostgreSQL connectivity
- **High retry count** — repeated failures, likely application or upstream issue

Example Prometheus alert:

```yaml
alert: MessageBridgeOutboxBacklog
expr: messagebridge_outbox_pending > 1000
for: 5m
labels:
  severity: warning
annotations:
  summary: "MessageBridge outbox has {{ $value }} pending messages"
  runbook: "https://wiki.internal/messagebridge/outbox-backlog"
```

## Incident Response

### Outbox Processing Stalled

1. Check logs for exceptions:
   ```bash
   kubectl logs -f deployment/messagebridge-worker --tail=100 | grep -i error
   ```

2. Verify RabbitMQ connectivity:
   ```bash
   curl -u admin:$RABBITMQ_PASS http://rabbitmq:15672/api/health/checks
   ```

3. Verify PostgreSQL:
   ```bash
   psql "host=$PG_HOST port=5432 dbname=messagebridge user=app" -c "SELECT 1;"
   ```

4. Check outbox table for stuck messages:
   ```sql
   SELECT COUNT(*), Status, MIN(CreatedAt), MAX(CreatedAt)
   FROM MessageBridgeOutbox
   GROUP BY Status;
   ```

5. If stuck messages exist with old timestamps, manually retry or mark as failed:
   ```sql
   UPDATE MessageBridgeOutbox
   SET Status = 'Pending', AttemptCount = 0
   WHERE MessageId = 'stuck-message-uuid'
     AND Status = 'Failed';
   ```

6. Restart worker pod to pick up changes:
   ```bash
   kubectl rollout restart deployment/messagebridge-worker
   ```

### Messages in Error Queue

1. Inspect via UI: `http://rabbitmq:15672 → Queues → messagebridge.errors`
2. View message details (payload, failure reason)
3. Fix upstream issue (validation, network, etc.)
4. Requeue to appropriate worker queue
5. Monitor for success in logs

### High Retry Rates

1. Check downstream service (WhatsApp API, email provider) status
2. Verify network/firewall rules
3. Check rate limits (backoff if needed)
4. Inspect failure logs for pattern:
   ```bash
   grep "retry" /var/log/messagebridge/*.json | jq '.failureReason' | sort | uniq -c
   ```

## See Also

- [Local Development](local-development.md) — running locally with Docker Compose
- [Deployment](deployment.md) — production setup, secrets, scaling
- [Message Contracts](contracts.md) — protobuf definitions & versioning
- [Publisher Guide](../src/MessageBridge.Publisher/README.md) — API usage, registration

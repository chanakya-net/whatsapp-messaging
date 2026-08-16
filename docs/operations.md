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

The worker exports structured logs, traces, and metrics directly to New Relic by OTLP/HTTP at `https://otlp.nr-data.net:4318`. Its service name is `MessageBridge.Worker`; `/metrics` is deliberately not exposed and returns `404`.

`OTEL_EXPORTER_OTLP_HEADERS` is populated from the environment vault's `new-relic-otlp-headers` secret through a versionless Key Vault reference. Store the complete New Relic header value there (for example, the required API-key header), never in OpenTofu variables, command lines, or application configuration.

### Azure-native platform alerts

Set the required non-secret `TF_VAR_alert_email` bootstrap input for shared, dev, and prod plans. Each root creates one common-schema email Action Group and fans every native metric alert in that state out to that address. Keep this address on a monitored platform distribution list.

These alerts use Azure Monitor platform metrics only. They do not create a Log Analytics workspace, Application Insights, diagnostic settings, scheduled-query alerts, alert-processing rules, or activity-log alerts.

| Alert | Meaning | First response |
|---|---|---|
| PostgreSQL `cpu_percent > 80` | Sustained database CPU saturation. | Check active connections and recent workload changes; stop a runaway workload before considering a SKU change. |
| PostgreSQL `cpu_credits_remaining < 30` | The Burstable B1ms server is close to losing burst capacity. | Reduce load and confirm credits recover; review whether sustained load requires a non-burstable SKU. |
| PostgreSQL `active_connections > 40` | Connections are nearing the B1ms limit of about 50. | Find leaking or idle clients, check the worker pool size, and terminate only confirmed stale sessions. |
| PostgreSQL `storage_percent > 80` | Fixed 32 GiB storage is nearing capacity; auto-grow is disabled. | Identify fast-growing tables/indexes, remove only reviewed disposable data, and plan a storage increase before 100%. |
| PostgreSQL `is_db_alive < 1` using `Maximum` | No alive sample appeared in the 15-minute window. | Check Azure resource health and server state, then test TLS connectivity from the affected environment. |
| Worker `Replicas < 1` | The private worker, fixed at one replica, has no running capacity. | Inspect revision state and system logs, then restart or roll back the unhealthy revision. Do not increase replica count. |
| Worker `RestartCount > 3` | The native cumulative replica restart counter indicates repeated restarts. | Inspect the current revision and restart timestamps; compare against deployment time before deciding whether this is a new crash loop. |
| Worker `WorkingSetBytes > 966367642` | Average working set exceeded about 90% of the 1 GiB limit. This is an OOM-risk proxy, not proof of an OOM kill. | Check memory trend and recent payload/workload changes; use live system logs to attribute an OOM because Azure exposes no native Container App OOM metric. |
| Job `Executions >= 1`, dimension `state=Failed` | A migration or smoke job execution failed. | List job execution history, identify the failed execution, and inspect its safe console output. Forward-fix migrations; never assume application delivery rolls them back. |

The native metric catalog is intentionally narrow: PostgreSQL server saturation/availability, worker capacity/restart/memory risk, and failed Container Apps jobs. Verify a disputed platform signal with `az monitor metrics list-definitions` against the resource before changing the catalog.

New Relic owns application-level error rate, latency, retry, readiness, trace, log, and application metric alerts. Azure-native alerts must not duplicate those signals. In particular, readiness failures, outbox backlog, publish failures, provider failures, and endpoint latency remain New Relic responsibilities.

During the first full billing month after rollout:

1. Confirm Azure Cost Management shows only expected Azure Monitor metric-alert and Action Group usage, with no Log Analytics ingestion, Application Insights, managed Prometheus, or query-alert charges.
2. Compare alert volume with incidents; tune reviewed thresholds through module inputs only when evidence shows sustained noise or missed platform risk.
3. Confirm each root still has one email Action Group and no duplicate receivers or orphaned alerts.
4. Record the review date, Azure cost delta, alert counts by rule, threshold decisions, and owner in the platform operations record.

### New Relic validation

After a worker revision is healthy, use New Relic to confirm recent data for `service.name = 'MessageBridge.Worker'` in all three signal types: logs, spans, and metrics. A small known-safe request to `/health/live` can create an ASP.NET Core trace; do not use a real recipient or a header value as a diagnostic probe.

Confirm the deployed wiring without reading secret values:

```bash
az containerapp show \
  --name "$WORKER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "properties.template.containers[0].env[?name=='OTEL_EXPORTER_OTLP_HEADERS'].{name:name,secretRef:secretRef}" \
  --output table
```

The output must show only `new-relic-otlp-headers` as the secret reference. It must not show a header value. There is no Log Analytics workspace, Application Insights resource, or Azure-managed Prometheus service for application telemetry.

### Live Container Apps logs

Use the console stream for worker logs and the system stream for revision diagnostics:

```bash
az containerapp logs show \
  --name "$WORKER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --type console \
  --tail 100 \
  --follow

az containerapp logs show \
  --name "$WORKER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --type system \
  --tail 100 \
  --follow
```

Logs and health responses must never contain RabbitMQ URIs, database usernames/passwords or tokens, OTLP headers/API keys, authorization values, payloads, or recipient email addresses and phone numbers. Stop collection and treat the output as an incident if any such value appears; rotate the exposed credential and remove the captured output from its storage location.

### Header replacement

Replace the value of `new-relic-otlp-headers` in the matching environment Key Vault; keep its reference URI versionless. Container Apps retrieves the latest version within 30 minutes and restarts active revisions that consume the secret in an environment variable. Do not put the replacement value in a deployment command, OpenTofu plan, or log. Verify the recovered worker with the safe reference query and New Relic signal checks above.

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

## Database Restore Drill

A quarterly point-in-time restore drill validates that PostgreSQL backups
are usable and records RPO/RTO evidence, without touching the production
server. See [Database Restore Drill runbook](runbooks/database-restore.md)
for prerequisites, execution steps, and failure/escalation handling.

A failed migration is never automatically reversed by application
delivery; recovery requires inspection and either a forward-fix migration
or an operator-performed point-in-time restore.

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
- [Database Restore Drill](runbooks/database-restore.md)

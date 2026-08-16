# Deployment Guide

Production deployment of MessageBridge: container configuration, CloudAMQP TLS, secrets management, scaling, and HITL (human-in-the-loop) decisions.

## Architecture Overview

```text
┌─────────────────────────────────────────┐
│  Azure Container Apps Worker            │
│  └─ MessageBridge Worker Service        │
│     ├─ Immutable digest (no mutable tag)│
│     ├─ Health Endpoints (port 8080)     │
│     └─ Graceful Shutdown Support        │
└────────────────┬────────────────────────┘
                 │ TLS
        ┌────────▼────────┐
        │  CloudAMQP      │
        │  (RabbitMQ SaaS)│
        └────────┬────────┘
                 │
        ┌────────▼────────┐
        │  Azure Database │
        │  for PostgreSQL │
        └─────────────────┘

Infrastructure: OpenTofu (.tofu/bootstrap, .tofu/envs/{dev,prod})
Key Vault: Versionless secret references
Migration: Manual Container Apps job (pre-deployment)
Delivery: GitHub workflow (delivery.yml)
```

## Container Image

### Building

The worker image repository is `ghcr.io/chanakya-net/whatsapp-messaging/worker`.
Deploy only an immutable digest, never a mutable tag. The runtime listens on port 8080,
serves `/health/live` and `/health/ready`, and always runs as the non-root `APP_UID` user.

```bash
# Build and load the host architecture for local testing.
docker build -f src/MessageBridge.Worker/Dockerfile -t messagebridge:local .

# Build both supported architectures and publish an immutable manifest.
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:<release-tag> \
  --push .

# Resolve the published digest, then deploy this exact reference.
WORKER_IMAGE='ghcr.io/chanakya-net/whatsapp-messaging/worker@sha256:<published-64-character-digest>'
```

### Migration image (manual only)

The migration image `ghcr.io/chanakya-net/whatsapp-messaging/migrate` is intended to be
invoked only by the manual Azure Container Apps migration job.

- It is a dedicated container that executes the EF Core migration bundle for
  `MessageBridgeDbContext`.
- Worker startup must not call `Database.Migrate*` or reference this migration image.
- The migration image should never be used as the worker container image or run as part of normal worker lifecycle.

### Image Size Optimization

- Use the digest-pinned ASP.NET runtime image (not the SDK)
- Trim unused assemblies: `<PublishTrimmed>true</PublishTrimmed>` in `.csproj`
- Result: ~150–200 MB per image

### Running Locally

```bash
# Build image
docker build -f src/MessageBridge.Worker/Dockerfile -t messagebridge:local .

# Run container with configuration via environment / JSON
docker run \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e "ConnectionStrings__DefaultConnection=Host=postgres.local;Port=5432;Database=messagebridge;Username=app;Password=$(cat /run/secrets/db_password);" \
  -e "MESSAGEBRIDGE_CONNECTION_STRING=Host=postgres.local;Port=5432;Database=messagebridge;Username=app;Password=$(cat /run/secrets/db_password);" \
  -e "RabbitMq__Host=rabbitmq.local" \
  -e "RabbitMq__Port=5671" \
  -e "RabbitMq__Username=admin" \
  -e "RabbitMq__Password=$(cat /run/secrets/rabbitmq_password)" \
  -e "RabbitMq__VirtualHost=/" \
  -e "RabbitMq__UseSsl=true" \
  -e "Observability__ServiceName=MessageBridge.Worker" \
  -e "Observability__MetricsEndpointEnabled=true" \
  messagebridge:local
```

## Environment Configuration

### Required Configuration

The worker binds configuration from `appsettings.json` and environment variables using the [Options pattern](https://learn.microsoft.com/en-us/dotnet/core/extensions/options).

PostgreSQL uses two bindings:

- `MESSAGEBRIDGE_CONNECTION_STRING` feeds the processing store and outbox startup path in `MessageBridge.Infrastructure`
- `ConnectionStrings:DefaultConnection` feeds the DbContext used by MassTransit registration and the readiness probe in `MessageBridge.Worker`

| Setting                                 | Path in Config                         | Example                         | Notes                                                                     |
| --------------------------------------- | -------------------------------------- | ------------------------------- | ------------------------------------------------------------------------- |
| Environment                             | `ASPNETCORE_ENVIRONMENT`               | `Production`                    | Controls `appsettings.json` variant                                       |
| Processing store connection             | `MESSAGEBRIDGE_CONNECTION_STRING`      | `Host=postgres.cloud;...`       | Read by `AddMessageBridgeProcessingStore`                                 |
| Worker DbContext / readiness connection | `ConnectionStrings:DefaultConnection`  | `Host=postgres.cloud;...`       | Read by MassTransit registration and `PostgresReadinessProbe`             |
| RabbitMQ host                           | `RabbitMq:Host`                        | `amqp-broker-123.cloudamqp.com` | Read by AddMessageBridgeMassTransit                                       |
| RabbitMQ port                           | `RabbitMq:Port`                        | `5671`                          | Use 5671 with `RabbitMq:UseSsl=true`; 5672 is plaintext (not recommended) |
| RabbitMQ username                       | `RabbitMq:Username`                    | (from secret)                   | CloudAMQP username                                                        |
| RabbitMq password                       | `RabbitMq:Password`                    | (from secret)                   | CloudAMQP password (DO NOT commit)                                        |
| RabbitMQ vhost                          | `RabbitMq:VirtualHost`                 | `/`                             | CloudAMQP vhost (usually `/`)                                             |
| RabbitMQ TLS                            | `RabbitMq:UseSsl`                      | `true`                          | Enable TLS for CloudAMQP                                                  |
| Observability service name              | `Observability:ServiceName`            | `MessageBridge.Worker`          | Service name for traces and metrics                                       |
| Observability OTLP endpoint             | `Observability:OtlpEndpoint`           | `http://otlp-collector:4318`    | Optional; if provided, enables distributed traces                         |
| Observability metrics endpoint          | `Observability:MetricsEndpointEnabled` | `true`                          | Enable `/metrics` Prometheus endpoint                                     |

### Configuration Examples

These examples show the worker's runtime configuration. `MessageBridge.Publisher` and `MessageBridge.Outbox` options are configured in application startup code when you consume those packages; they are not auto-bound from this file by the worker.

#### appsettings.Production.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "MessageBridge": "Information"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres.cloud.example.com;Port=5432;Database=messagebridge;Username=app;Password=from-secret;SslMode=Require;"
  },
  "RabbitMq": {
    "Host": "amqp-broker-123.cloudamqp.com",
    "Port": 5671,
    "Username": "from-secret",
    "Password": "from-secret",
    "VirtualHost": "/",
    "UseSsl": true
  },
  "Observability": {
    "ServiceName": "MessageBridge.Worker",
    "MetricsEndpointEnabled": true,
    "OtlpEndpoint": "http://otlp-collector:4318/v1/traces"
  },
  "MessageBridge": {
    "Topology": {
      "EnvironmentPrefix": "prod"
    },
    "TransportRetry": {
      "ImmediateRetryCount": 3,
      "DelayedRedeliveryIntervals": ["00:05:00", "00:15:00", "01:00:00"]
    },
    "ProcessingHistory": {
      "RecoveryEnabled": true,
      "StaleThresholdMinutes": 30,
      "CleanupEnabled": true,
      "CleanupRetentionHours": 24,
      "CleanupBatchSize": 500,
      "CleanupIntervalMilliseconds": 1000
    }
  }
}
```

### Azure Container Apps Configuration

Production deployment uses Azure Container Apps with workload identity for secure secret access:

- Workload identity (user-assigned managed identity) handles authentication to Key Vault
- Container Apps runtime reads versionless secret URIs automatically
- No secret values appear in environment variables, logs, or command lines
- See [Bootstrap and Deployment Runbook](./runbooks/deployment.md) for operational details

## Secrets Management

All secrets are stored in Azure Key Vault with versionless references.

⚠️ **Security Critical**: Never pass secrets via command-line arguments, environment variables, logs, or version control.

### Approved Secret Names

These are the only secrets used by MessageBridge:

- `rabbitmq-connection-string` — full CloudAMQP connection URI
- `new-relic-otlp-headers` — New Relic OTLP authentication headers
- `whatsapp-provider-placeholder` — disabled placeholder (absent in production)
- `email-provider-placeholder` — disabled placeholder (absent in production)

### Key Vault Configuration

1. **Create Key Vault** (done by OpenTofu bootstrap)
   - Versionless secret URI: `https://<vault-name>.vault.azure.net/secrets/<secret-name>`
   - Workload identity grants read permission to approved secret names
   - No list/delete/purge permissions for production runtime

2. **Rotation**:
   - Create new secret version in Key Vault (out of band, not in runbooks)
   - Workers fetch versionless URI automatically (gets latest version)
   - Restart workers via `delivery.yml reload-secrets` job
   - Old credentials continue working during grace period

3. **Access Control**:
   - Workload identity (managed by OpenTofu) has least-privilege read access
   - Audit Key Vault access via Azure Activity Log
   - Alert on unauthorized secret access

See [Secret Rotation Runbook](./runbooks/secret-rotation.md) for operational procedures.

## CloudAMQP Configuration

MessageBridge connects to CloudAMQP (managed RabbitMQ) in production.

### Instance Setup

1. **Create CloudAMQP instance**:
   - Plan: Lemur (free), Tiger, Rabbit, Panda (depending on throughput)
   - Region: closest to application servers
   - TLS: enabled (required for security)

2. **Extract connection details**:
   - **AMQP URL**: `amqps://user:pass@broker.cloudamqp.com:5671/vhost`
   - **Host**: `broker.cloudamqp.com`
   - **Port**: `5671` (TLS) or `5672` (plaintext, not recommended)
   - **Username**: from CloudAMQP dashboard
   - **Password**: from CloudAMQP dashboard
   - **Virtual host**: usually `/`

### TLS Configuration

CloudAMQP requires TLS for production deployments.

- If you configure decomposed fields (`RabbitMq:Host`, `RabbitMq:Port`, `RabbitMq:Username`, `RabbitMq:Password`, `RabbitMq:VirtualHost`), set `RabbitMq:UseSsl=true` to enable TLS.
- If you configure `RabbitMq:ConnectionString`, use an `amqps://` URI to select TLS explicitly.
- Port `5671` is the usual TLS port, but the port number alone does not turn TLS on in the decomposed configuration path.

The current transport wiring enables TLS only when the options require it, so the documentation must match the configuration shape you choose.

### Certificate Pinning (Advanced)

For extra security, pin the CloudAMQP certificate:

```csharp
// Not typically required; CloudAMQP uses standard CAs
// Only needed if using self-signed certificates (not recommended)
```

## Database Configuration

### PostgreSQL Setup

1. **Create cloud database** (AWS RDS, Azure Database, Google Cloud SQL, etc.)
   - Version: PostgreSQL 14+
   - Backups: automated daily
   - Replication: multi-AZ (high availability)
   - Encryption: at rest and in transit

2. **Create application user** (least privilege):

   ```sql
   CREATE USER app WITH PASSWORD 'generated-secure-password';
   CREATE DATABASE messagebridge OWNER app;
   GRANT USAGE ON SCHEMA public TO app;
   GRANT CREATE ON SCHEMA public TO app;
   ```

3. **Connection string**:

   ```text
   Host=postgres.rds.amazonaws.com;Port=5432;Database=messagebridge;Username=app;Password=...;SslMode=Require;
   ```

### Migrations

Apply EF Core migrations as a dedicated pre-deployment action.
Worker startup must never call `Database.Migrate*`.

The migration path is:

1. Build and publish the dedicated migration container image:
   `ghcr.io/chanakya-net/whatsapp-messaging/migrate`
2. Run the container in a manual Azure Container Apps migration job.
3. Only start the worker Deployment after migration completes successfully.

During migration, pass non-secret `Database__*` settings (for example `Database__Host`, `Database__Database`, `Database__Username`, `Database__Password`) through job environment and secrets.

This image contains only the generated migration bundle and does not launch `MessageBridge.Worker`.

```bash
dotnet ef database update --project src/MessageBridge.Infrastructure --startup-project src/MessageBridge.Worker --connection "Host=prod-postgres;Database=messagebridge;Username=app;Password=...;"
```

For the current deployment process, avoid keeping schema drift at startup; migrations are handled only by the migration job.

```bash
dotnet ef database update --project src/MessageBridge.Infrastructure --startup-project src/MessageBridge.Worker --connection "Host=prod-postgres;Database=messagebridge;Username=app;Password=...;"
```

## Azure Container Apps Deployment

Production deployment is managed via OpenTofu. All container configuration is defined in `.tofu/modules/worker-workload/main.tf` and provisioned by the OpenTofu infrastructure code.

### Deployment Model

- **Worker image**: Immutable digest-pinned reference (never mutable tags)
- **Migration job**: Manual, independent Container Apps job triggered before deployment
- **Scaling**: 1–5 replicas with automatic scaling based on CPU and memory metrics
- **Health checks**: Startup, liveness, and readiness probes on port 8080
- **Workload identity**: User-assigned managed identity for Key Vault secret access
- **Secrets**: Versionless Key Vault references (updated without container restart)

See [Bootstrap and Deployment Runbook](./runbooks/deployment.md) for step-by-step operational procedures.

### Delivery and Release

- **Automated**: push to main → build images → test → deploy to dev
- **Manual approval**: promotion to production via `delivery.yml` workflow
- **Rollback**: revert to prior image digest via `delivery.yml` or OpenTofu
- **Secrets reload**: `delivery.yml reload-secrets` job updates all instances without restart

## Monitoring & Observability

### Observability

Use the `Observability` section to control traces and the Prometheus scrape endpoint:

```json
{
  "Observability": {
    "ServiceName": "MessageBridge.Worker",
    "MetricsEndpointEnabled": true,
    "OtlpEndpoint": "http://otlp-collector:4318/v1/traces"
  }
}
```

### Observability Configuration

Configure observability via environment variables and Key Vault references:

```json
{
  "Observability": {
    "ServiceName": "MessageBridge.Worker",
    "MetricsEndpointEnabled": true,
    "OtlpEndpoint": "https://otlp-collector.example.com/v1/traces"
  }
}
```

See [Operations Guide](./operations.md) for monitoring setup and alert configuration.

## Graceful Shutdown

MessageBridge handles graceful shutdown to avoid message loss via Azure Container Apps lifecycle hooks:

- Container runtime sends SIGTERM on revision termination
- Application processes in-flight requests
- Container is forcibly killed after termination grace period (default 30s)

## Package Feed Configuration

**MessageBridge.Publisher** is distributed via a private NuGet feed.

### Client Setup

Configure `nuget.config` in your consuming application:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="messagebridge" value="https://your-feed.pkgs.visualstudio.com/_packaging/messagebridge/nuget/v3/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <messagebridge>
      <add key="Username" value="[PAT]" />
      <add key="ClearTextPassword" value="[PAT]" />
    </messagebridge>
  </packageSourceCredentials>
</configuration>
```

**HITL Decision**: Package feed location and authentication are environment-specific. Update feed URL and credentials per deployment environment.

## Manual delivery operations

Use `.github/workflows/delivery.yml` for every manual image or Azure application operation. The
workflow accepts only the bounded `target` and `environment` choices below. It does not accept an
image digest, tag, secret name, credential, connection string, token, or secret value.

| Target | Environment input | Scope and result |
|---|---|---|
| `publish` | `none` | Validates, builds, scans, pushes, attests, and anonymously verifies both public GHCR images. Azure is not accessed. |
| `dev` | `none` | Publishes when required, then runs the existing development migration, exact-digest worker release, health check, smoke check, and application rollback path. |
| `prod` | `none` | Completes the same verified development release and immutable handoff, then waits for protected production approval and promotes those exact digests. |
| `reload-secrets` | `dev` | Validates current versionless Key Vault references and reloads the development worker at its current digest. It does not publish, migrate, or apply infrastructure. |
| `reload-secrets` | `prod` | Performs the same current-digest reload behind the protected production approval and concurrency boundary. |

Invoke a target from an authenticated GitHub CLI session, replacing `<delivery-ref>` with the commit
or protected branch to operate:

```bash
gh workflow run delivery.yml --ref <delivery-ref> -f target=publish -f environment=none
gh workflow run delivery.yml --ref <delivery-ref> -f target=dev -f environment=none
gh workflow run delivery.yml --ref <delivery-ref> -f target=prod -f environment=none
gh workflow run delivery.yml --ref <delivery-ref> -f target=reload-secrets -f environment=dev
gh workflow run delivery.yml --ref <delivery-ref> -f target=reload-secrets -f environment=prod
```

Before running an operation:

1. Ensure repository validation is green and the worker and migration GHCR packages are public so
   the publication job can perform anonymous digest verification.
2. Configure the `dev` and `prod` GitHub Environments with their environment-specific Azure OIDC
   client IDs and reviewed OpenTofu backend coordinates. Configure required reviewers for production;
   apply the same reviewer policy to development if local policy requires it.
3. Keep the workflow's stable, non-cancelling concurrency settings enabled. A manual operation is
   serialized with automatic delivery and cannot overtake another state mutation.
4. For a reload, update secret values in Key Vault out of band first. Operators must never pass a secret value to the workflow,
   place one in a dispatch field, or paste one into a run summary.

`publish` ends after anonymous digest verification. `dev` may pause for development Environment
approval. `prod` first requires a successful development migration, healthy revision, smoke check,
and no rollback; it then pauses for required reviewers on the `prod` GitHub Environment before any
production OIDC token is issued. Reloads use only the selected environment identity; production
reload approval also occurs before production OIDC issuance.

The manual summary is deliberately limited to environment, immutable digest, execution/health/smoke
status, and rollback status. Interpret failures as follows:

- Validation, scan, attestation, push, or anonymous-verification failure means no Azure deployment
  was attempted.
- Development failure prevents production promotion. A worker health or smoke failure invokes the
  existing application rollback; migration failure stops before worker mutation.
- Reload rejects missing, extra, identity-mismatched, or versioned Key Vault references before worker
  mutation. After mutation begins, revision creation, health, or smoke failure triggers reload rollback to the captured prior revision.
  Reload rollback succeeds only after that prior revision is active,
  healthy at the captured digest, and passes the internal smoke job.
- Release rollback restores only the prior worker image, and reload rollback restores the verified
  prior revision. Neither path ever reverses the database schema; migration recovery requires a
  forward fix or the approved database recovery procedure.

## Protected production promotion

`.github/workflows/delivery.yml` is the sole ordered application delivery path. A successful
development release writes a one-day, run-attempt-specific promotion artifact containing only
the commit, development-tested worker and migration digests, and sanitized migration, health,
smoke, and rollback results. Production validates that artifact against explicit development job
outputs. It does not rebuild, retag, copy, or resolve either image through a mutable tag.

Configure the repository's `prod` GitHub Environment before enabling promotion:

1. Add required reviewers and restrict deployment branches to the intended delivery branch.
2. Disable administrator protection-rule bypass where repository policy permits it.
3. Prevent self-review when independent approval is required by policy.
4. Store the production Azure client ID and production OpenTofu backend coordinates as environment
   variables. Do not expose production deployment identity values outside protected `prod` jobs.

Required reviewer identities and bypass policy live in GitHub Environment settings; workflow YAML
can select `prod` but cannot declare those reviewers. GitHub evaluates the Environment gate before
starting the job, so approval occurs before production OIDC issuance and every production mutation.
Production promotions also use a stable, non-cancelling concurrency group so two approved attempts
cannot mutate production concurrently.

After approval, the workflow performs this fixed sequence:

1. Validate the immutable handoff's commit, exact digests, environment, and successful results.
2. Sign in with the production deployment identity and resolve runtime names from production state.
3. Update and run the migration job with the exact development-tested migration digest; wait for success.
4. Capture the current worker digest, update the worker to the exact development-tested worker digest,
   and wait for that digest to report healthy.
5. Run the internal production smoke job.

Migration failure stops before worker mutation. Worker update, health, or smoke failure triggers one
application rollback: restore the captured worker digest, wait for that exact prior digest to become
healthy, then rerun internal smoke. The workflow never reverses database schema automatically.
Migration recovery requires an operator-led forward fix or the approved database point-in-time restore
procedure.

The delivery summary reports commit, `prod` approval environment, development and production digests,
digest identity, migration, health, smoke, prior worker digest, and rollback result. It intentionally
omits secrets, environment configuration, backend coordinates, resource names, state, and logs.

## HITL (Human-In-The-Loop) Decisions

The following decisions require manual intervention and cannot be automated:

### 1. Real Provider & Credentials

- **Hosting target** (CloudAMQP, self-hosted RabbitMQ, etc.) — determined by ops team
- **RabbitMQ credentials** — managed by CloudAMQP or infrastructure team
- **Database** (cloud provider, self-hosted, etc.) — determined by architecture team

**Action**: Update environment variables and secrets provider accordingly.

### 2. Scaling & Capacity Planning

- **Replica count** — based on message throughput
- **Pod resource requests/limits** — based on profiling
- **Database instance size** — based on data volume & query patterns

**Action**: Monitor metrics, adjust HPA thresholds and replica counts.

### 3. Retention Policies

- **Outbox retention** (package default: 24 hours) — adjust based on compliance requirements
- **Backup frequency** — per data protection policy

**Action**: Update configuration, schedule backups, implement retention cleanup jobs.

### 4. Alert Thresholds

- **Outbox queue depth** — alert when > 1000 pending messages
- **Error queue depth** — alert when not empty
- **CPU/memory utilization** — alert at custom thresholds

**Action**: Configure alert rules in monitoring platform; add runbooks for incident response.

### 5. Secret Rotation Schedule

- **Credential rotation frequency** — every 90 days (or per policy)
- **Grace period** — how long old credentials remain valid

**Action**: Schedule rotation; update secrets provider; coordinate pod restarts.

## Troubleshooting Deployment

### Container fails to start

1. Check logs: `az containerapp logs show --resource-group <rg> --name <ca-name>`
2. Verify workload identity: `az identity show --resource-group <rg> --name <identity>`
3. Verify Key Vault access: confirm runtime identity has read permissions for approved secret names
4. Check provisioning state: `az containerapp show --query properties.provisioningState`

### OutOfMemory errors

1. Check Container App metrics: Azure Portal → Container Apps → Metrics
2. Increase limit in OpenTofu: `.tofu/modules/worker-workload/variables.tf`
3. Apply via `delivery.yml` workflow

### Revision swap failures

1. Check Container App history: `az containerapp revision list --resource-group <rg> --name <ca-name>`
2. Verify image digest exists and is accessible
3. Confirm workload identity has container registry pull permissions

For operational procedures, see [Troubleshooting Guide](./operations.md).

## See Also

- [Local Development](local-development.md) — running locally with Docker Compose
- [Operations](operations.md) — health checks, retries, error handling, cleanup
- [Message Contracts](contracts.md) — protobuf definitions & versioning

# Deployment Guide

Production deployment of MessageBridge: container configuration, CloudAMQP TLS, secrets management, scaling, and HITL (human-in-the-loop) decisions.

## Architecture Overview

```
┌─────────────────────────────────────────┐
│  Application (Kubernetes Pod)           │
│  └─ MessageBridge Worker Service        │
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
        │  PostgreSQL     │
        │  (Cloud DB)     │
        └─────────────────┘
```

## Container Image

### Building

```bash
# From repository root
dotnet publish src/MessageBridge.Worker -c Release -o ./publish

# Multi-stage Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10 AS runtime
WORKDIR /app
COPY publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "MessageBridge.Worker.dll"]
```

### Image Size Optimization

- Use base image: `mcr.microsoft.com/dotnet/aspnet:10` (not SDK)
- Trim unused assemblies: `<PublishTrimmed>true</PublishTrimmed>` in `.csproj`
- Result: ~150–200 MB per image

### Running Locally

```bash
# Build image
docker build -t messagebridge:latest .

# Run container
docker run \
  -p 8080:8080 \
  -e RABBITMQ_HOST=rabbitmq.local \
  -e RABBITMQ_PORT=5671 \
  -e RABBITMQ_USERNAME=admin \
  -e RABBITMQ_PASSWORD=$(cat /run/secrets/rabbitmq_password) \
  -e DATABASE_CONNECTION_STRING="Host=postgres.local;Port=5432;Database=messagebridge;Username=app;Password=$(cat /run/secrets/db_password);" \
  messagebridge:latest
```

## Environment Configuration

### Required Environment Variables

| Variable | Example | Notes |
|----------|---------|-------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Controls `appsettings.json` variant |
| `RABBITMQ_HOST` | `amqp-broker-123.cloudamqp.com` | CloudAMQP hostname |
| `RABBITMQ_PORT` | `5671` | Use 5671 for TLS, 5672 for plaintext (not recommended) |
| `RABBITMQ_USERNAME` | (from secret) | CloudAMQP username |
| `RABBITMQ_PASSWORD` | (from secret) | CloudAMQP password (DO NOT commit) |
| `RABBITMQ_VIRTUAL_HOST` | `/` | CloudAMQP vhost (usually `/`) |
| `DATABASE_CONNECTION_STRING` | (from secret) | PostgreSQL connection string |
| `MESSAGEBRIDGE_EXCHANGE_NAME` | `messagebridge.commands` | RabbitMQ exchange name |
| `MESSAGEBRIDGE_WHATSAPP_ROUTING_KEY` | `whatsapp.send` | Routing key for WhatsApp messages |
| `MESSAGEBRIDGE_EMAIL_ROUTING_KEY` | `email.confirmation` | Routing key for email messages |

### Configuration Examples

#### appsettings.Production.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "MessageBridge": "Information"
    }
  },
  "MessageBridge": {
    "Publisher": {
      "ExchangeName": "messagebridge.commands",
      "WhatsAppRoutingKey": "whatsapp.send",
      "EmailRoutingKey": "email.confirmation"
    },
    "Outbox": {
      "BatchSize": 100,
      "Concurrency": 4,
      "PollIntervalMilliseconds": 500,
      "MaxRetryAttempts": 3,
      "RetryDelayMilliseconds": 50,
      "RetryBackoffMultiplier": 2.0,
      "CleanupEnabled": true,
      "CleanupRetentionHours": 24,
      "CleanupBatchSize": 500,
      "CleanupIntervalMilliseconds": 1000
    }
  },
  "OpenTelemetry": {
    "Exporters": {
      "Jaeger": {
        "Endpoint": "http://jaeger-collector:4318/v1/traces"
      }
    }
  }
}
```

#### docker-compose.prod.yml (Reference)

```yaml
version: "3.8"

services:
  worker:
    image: messagebridge:latest
    ports:
      - "8080:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      RABBITMQ_HOST: ${RABBITMQ_HOST}
      RABBITMQ_PORT: 5671
      RABBITMQ_USERNAME_FILE: /run/secrets/rabbitmq_username
      RABBITMQ_PASSWORD_FILE: /run/secrets/rabbitmq_password
      DATABASE_CONNECTION_STRING_FILE: /run/secrets/db_connection_string
    secrets:
      - rabbitmq_username
      - rabbitmq_password
      - db_connection_string
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 3
      start_period: 30s
    restart: unless-stopped

secrets:
  rabbitmq_username:
    file: ${SECRETS_DIR}/rabbitmq_username.txt
  rabbitmq_password:
    file: ${SECRETS_DIR}/rabbitmq_password.txt
  db_connection_string:
    file: ${SECRETS_DIR}/db_connection_string.txt
```

## Secrets Management

⚠️ **Security Critical**: Never commit secrets to version control.

### Best Practices

1. **Use a secrets provider**:
   - Kubernetes Secrets (for Kubernetes deployments)
   - Azure Key Vault (for Azure deployments)
   - AWS Secrets Manager (for AWS deployments)
   - HashiCorp Vault (for self-hosted)

2. **Rotation**:
   - Rotate credentials every 90 days (or per compliance policy)
   - Update in secrets provider, restart pods
   - Old credentials continue working during grace period

3. **Access Control**:
   - Limit service account permissions to minimum required (least privilege)
   - Audit all secret access
   - Alert on unusual access patterns

### Kubernetes Secrets (Example)

```bash
# Create secret from literals
kubectl create secret generic messagebridge-secrets \
  --from-literal=RABBITMQ_USERNAME=admin \
  --from-literal=RABBITMQ_PASSWORD=$(openssl rand -base64 32) \
  --from-literal=DATABASE_CONNECTION_STRING="Host=postgres.cloud;..."

# Reference in deployment
kubectl apply -f - <<EOF
apiVersion: apps/v1
kind: Deployment
metadata:
  name: messagebridge-worker
spec:
  template:
    spec:
      containers:
      - name: worker
        image: messagebridge:latest
        envFrom:
        - secretRef:
            name: messagebridge-secrets
EOF
```

### Environment Variable Protection

To prevent secrets from leaking in logs:

```csharp
// In Program.cs
builder.Services.Configure<LoggerFilterOptions>(opts =>
{
    // Redact sensitive patterns in logs
    opts.Rules.Add(new LoggerFilterRule
    {
        CategoryName = "Microsoft.*",
        LogLevel = LogLevel.Debug,
    });
});
```

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

CloudAMQP requires TLS for security. MassTransit automatically enables TLS when connecting to port 5671.

```csharp
// In Program.cs
services.AddMassTransit(cfg =>
{
    cfg.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri($"amqps://{host}:{port}/{vhost}"), h =>
        {
            h.Username(username);
            h.Password(password);
            h.UseCluster(c => { }); // Optional: for HA setup
        });
        
        // TLS is automatically enabled for amqps:// scheme
        // CA certificate validation is enabled by default
    });
});
```

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
   ```
   Host=postgres.rds.amazonaws.com;Port=5432;Database=messagebridge;Username=app;Password=...;SslMode=Require;
   ```

### Migrations

Apply EF Core migrations on startup (or as a separate pre-deployment step):

```csharp
// In Program.cs (Startup)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
```

Or run migrations separately:

```bash
dotnet ef database update --project src/MessageBridge.Infrastructure --startup-project src/MessageBridge.Worker --connection "Host=prod-postgres;Database=messagebridge;Username=app;Password=...;"
```

## Kubernetes Deployment

### Deployment Manifest

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: messagebridge-worker
  namespace: production
  labels:
    app: messagebridge
    component: worker
spec:
  replicas: 3  # HA setup
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxSurge: 1
      maxUnavailable: 0
  selector:
    matchLabels:
      app: messagebridge
      component: worker
  template:
    metadata:
      labels:
        app: messagebridge
        component: worker
    spec:
      serviceAccountName: messagebridge
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
      containers:
      - name: worker
        image: messagebridge:latest
        imagePullPolicy: IfNotPresent
        ports:
        - containerPort: 8080
          name: http
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        envFrom:
        - secretRef:
            name: messagebridge-secrets
        resources:
          requests:
            memory: "512Mi"
            cpu: "250m"
          limits:
            memory: "1Gi"
            cpu: "1000m"
        livenessProbe:
          httpGet:
            path: /health/live
            port: http
          initialDelaySeconds: 30
          periodSeconds: 10
          timeoutSeconds: 3
          failureThreshold: 3
        readinessProbe:
          httpGet:
            path: /health/ready
            port: http
          initialDelaySeconds: 10
          periodSeconds: 5
          timeoutSeconds: 3
          failureThreshold: 1
        lifecycle:
          preStop:
            exec:
              command: ["/bin/sh", "-c", "sleep 15"]  # Graceful shutdown window
---
apiVersion: v1
kind: Service
metadata:
  name: messagebridge-worker
  namespace: production
spec:
  selector:
    app: messagebridge
    component: worker
  ports:
  - port: 8080
    targetPort: http
    protocol: TCP
  type: ClusterIP
---
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: messagebridge-worker
  namespace: production
spec:
  minAvailable: 2  # Always keep 2 pods running
  selector:
    matchLabels:
      app: messagebridge
      component: worker
```

### Scaling

Horizontal Pod Autoscaler (HPA):

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: messagebridge-worker
  namespace: production
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: messagebridge-worker
  minReplicas: 3
  maxReplicas: 10
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70
  - type: Resource
    resource:
      name: memory
      target:
        type: Utilization
        averageUtilization: 80
```

## Monitoring & Observability

### Metrics Export

Enable OpenTelemetry metrics export:

```json
{
  "OpenTelemetry": {
    "Exporters": {
      "PrometheusMetrics": {
        "Endpoint": "http://prometheus:9090"
      },
      "Jaeger": {
        "Endpoint": "http://jaeger-collector:4318/v1/traces"
      }
    }
  }
}
```

### Prometheus Scrape Config

```yaml
scrape_configs:
  - job_name: 'messagebridge'
    kubernetes_sd_configs:
    - role: pod
      namespaces:
        names:
        - production
    relabel_configs:
    - source_labels: [__meta_kubernetes_pod_label_app]
      action: keep
      regex: messagebridge
```

### Log Aggregation

Configure Serilog to output structured logs:

```csharp
// In Program.cs
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentUserName()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Application", "MessageBridge.Worker")
    .WriteTo.Console(new JsonFormatter())  // JSON to stdout
    .CreateLogger();
```

Forward logs to your aggregation platform (Datadog, Splunk, ELK, etc.):

```bash
# Example: Datadog agent in sidecar
kubectl apply -f - <<EOF
apiVersion: v1
kind: ConfigMap
metadata:
  name: datadog-config
data:
  datadog.yaml: |
    logs_enabled: true
    container_collect_all: true
EOF
```

## Graceful Shutdown

MessageBridge handles graceful shutdown to avoid message loss:

```csharp
// In Program.cs
var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(() =>
{
    // BackgroundService instances are stopped by the host.
    app.Logger.LogInformation("Application stopping; hosted outbox services will drain during shutdown.");
});

app.Run();
```

Kubernetes sends SIGTERM on pod termination:
1. `preStop` hook waits 15 seconds (for load balancer drain)
2. Application processes in-flight requests
3. Pod is forcibly killed after `terminationGracePeriodSeconds` (default 30s)

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

- **Outbox retention** (default 7 days) — adjust based on compliance requirements
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

### Pod not starting

1. Check logs: `kubectl logs deployment/messagebridge-worker`
2. Check events: `kubectl describe pod <pod-name>`
3. Verify secrets exist: `kubectl get secrets | grep messagebridge`
4. Test image locally: `docker run messagebridge:latest /bin/sh`

### OutOfMemory errors

1. Check usage: `kubectl top pod <pod-name>`
2. Increase limit: edit Deployment, set `limits.memory: 2Gi`
3. Profile in staging before deploying

### Connection timeouts

1. Verify CloudAMQP is reachable: `nslookup broker.cloudamqp.com`
2. Check firewall: test port 5671 from pod
3. Verify TLS certificate: `openssl s_client -connect broker.cloudamqp.com:5671`

## See Also

- [Local Development](local-development.md) — running locally with Docker Compose
- [Operations](operations.md) — health checks, retries, error handling, cleanup
- [Message Contracts](contracts.md) — protobuf definitions & versioning

# Bootstrap and Deployment Runbook

Complete human-in-the-loop bootstrap from empty Azure/GitHub setup through production-ready MessageBridge platform. All twelve HITL stages are documented here and in related runbooks.

## Prerequisites and Authentication

### Stage 1: Authentication

Ensure these tools are installed and authenticated before bootstrap:

- **Azure CLI** (`az`) logged in as a principal with subscription-level permissions
- **GitHub CLI** (`gh`) authenticated with repo access and GHCR token
- **OpenTofu** (`tofu`) v1.7+ for IaC deployment
- **PostgreSQL client** (`psql`) for database operations (optional, for inspection)
- **Docker** for image publication and local testing

Verify authentication:

```bash
az account show | jq -r .id
gh auth status
ghcr_token=$(gh auth token)
# Validate GHCR token has write:packages scope
curl -s -H "Authorization: Bearer $ghcr_token" \
  https://ghcr.io/v2/chanakya-net/whatsapp-messaging/worker/blobs/uploads/ \
  | head -c 50
```

Required Azure roles on the target subscription:
- `Contributor` (or equivalent for all resource types)
- `Key Vault Administrator`
- `Azure Database for PostgreSQL Flexible Server Contributor`

## Input Collection and Naming

### Stage 2: Input Collection

Create a file `bootstrap.env` with these values (copy/paste-safe template):

```bash
# Environment identifier
ENVIRONMENT=dev  # or prod
BOOTSTRAP_SERIAL=042       # three-digit environment serial (001-999)
REGION=centralindia        # Azure region (e.g., eastus, australiaeast)

# Azure subscription and tenant
AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000
AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000

# GitHub GHCR configuration
GHCR_USERNAME=chanakya-net
GHCR_REPO=whatsapp-messaging

# Derived automatically (do not edit)
MESSAGEBRIDGE_BOOTSTRAP_SERIAL=$BOOTSTRAP_SERIAL
```

Load environment:

```bash
set -a
source bootstrap.env
set +a
```

### Stage 3: Naming and Region Checks

Validate environment before bootstrap:

```bash
# Check serial format
[[ "$BOOTSTRAP_SERIAL" =~ ^[0-9]{3}$ ]] || \
  echo "ERROR: BOOTSTRAP_SERIAL must be 3 digits (001-999), got $BOOTSTRAP_SERIAL"

# Check region exists
az account list-locations --query "[?name=='$REGION'].displayName" || \
  echo "ERROR: Region not valid: $REGION"

# Verify subscription
az account show --query id -o tsv
```

Record these values — they identify all bootstrap state and resources.

## Bootstrap Phase: State and OIDC

### Stage 4: State and OIDC Setup

Run the unified bootstrap script which handles state backend, OIDC, and foundational infrastructure:

```bash
cd "$(git rev-parse --show-toplevel)"
bash scripts/infra/bootstrap.sh
```

This script:
- Creates Azure storage account for OpenTofu state
- Configures GitHub OIDC for CI/CD
- Applies foundational infrastructure (.tofu/bootstrap)
- Outputs Key Vault, database, container environment URIs

Expected output includes resource group, storage account, and state container names. Record these for future operations.

**Review approval gate**: Before this step progresses, review the OpenTofu plan output:

```bash
# The bootstrap script displays: tofu plan output
# Expected: creation of identity, storage account, network, OIDC, Key Vault
# Unexpected: any deletion of existing resources (abort and investigate)
```

## Foundation Phase: Dummy Secrets and Database

### Stage 5: Placeholder Secret Seeding

Initialize Key Vault with approved placeholder secrets before workloads reference them:

```bash
bash scripts/infra/seed-placeholder-secrets.sh \
  --vault-name "kv-msgbr-${ENVIRONMENT}-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
```

Expected secrets created (placeholder values only):
- `rabbitmq-connection-string`
- `new-relic-otlp-headers`
- `whatsapp-provider-placeholder`
- `email-provider-placeholder`

These are versionless secrets. Do not use placeholder values in production; they signal missing real configuration.

Expected output: secret names and URIs (no secret values displayed).

**Note**: Real secret values arrive out of band from Key Vault portal, never via runbook commands.

### Stage 6: Database Grants and Identity

Database is provisioned by OpenTofu via `.tofu/envs/{dev,prod}` apply. Confirm it exists:

```bash
db_name="psql-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
db_rg="rg-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

# Verify database exists
az postgres flexible-server show \
  --resource-group "$db_rg" \
  --name "$db_name" \
  --query provisioningState -o tsv
```

Expected output: `Succeeded`

Database is ready. The OpenTofu configuration grants the migration job and worker workload identities the required permissions. No manual grant statements are needed.

## Workload Phase: Messaging and Observability

### Stage 7: CloudAMQP Configuration

CloudAMQP credentials are managed out of band (via CloudAMQP SaaS portal). Record and store them:

1. **Create or record CloudAMQP instance** (from CloudAMQP portal)
   - Instance URL and credentials provided out of band
   - Example: `amqps://user:pass@instance-id.cloudamqp.com:5671/vhost`

2. **Store connection string in Key Vault** (via Azure portal, not via runbook)
   - Secret name: `rabbitmq-connection-string`
   - Value: full CloudAMQP connection URI
   - Keep value out of terminal history and logs

3. **Verify CloudAMQP setup** (via CloudAMQP portal):
   - Virtual host `/` exists
   - Default user has all permissions
   - No stale queues from prior deployments

See [CloudAMQP Outage Runbook](./cloudamqp-outage.md) for failure procedures.

### Stage 8: New Relic and Observability

New Relic integration is optional. If enabled:

1. **Create New Relic application** (via New Relic portal)
   - Organization and API key provided out of band

2. **Store New Relic headers in Key Vault** (via Azure portal)
   - Secret name: `new-relic-otlp-headers`
   - Value: OTLP auth headers (example: `api-key=<LICENSE_KEY>`)
   - Keep value out of terminal history and logs

3. **Verify telemetry flow** (after first deployment):
   - Check New Relic dashboard for MessageBridge service entity
   - Confirm transactions and traces are received

If New Relic is disabled, the placeholder value `api-key=PLACEHOLDER_NOT_A_REAL_KEY` remains in Key Vault.

### Stage 9: Container Registry Visibility

Ensure worker image is publicly readable from GitHub Container Registry:

```bash
# Check current image package visibility
gh api repos/chanakya-net/whatsapp-messaging/packages \
  --jq '.[] | select(.name | contains("worker")) | {name, visibility}'
```

If image is private, change via GitHub portal:
- Settings → Packages → Package settings → Change visibility to public

Or via API (if permissions allow):

```bash
# Requires specific package admin permissions
gh api repos/chanakya-net/whatsapp-messaging/packages/<package-id> \
  -X PATCH -f visibility=public
```

## Deployment Phase: Release and Promotion

### Stage 10: First Release (Dev)

Build and publish worker image:

```bash
# Build for both architectures and publish
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.0-dev \
  --push .

# Resolve published image digest
worker_image="ghcr.io/chanakya-net/whatsapp-messaging/worker@$(
  gh api repos/chanakya-net/whatsapp-messaging/packages \
    --jq '.[] | select(.name | contains("worker")) | .latest_version.docker_image_metadata.layers[0].digest'
)"
```

Deploy to dev using delivery workflow:

```bash
# Trigger dev deployment via GitHub Actions
gh workflow run delivery.yml \
  -f target=dev \
  -f environment=none
```

Verify dev deployment health:

```bash
# Get deployment URL
dev_ca_name="ca-messagebridge-dev-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
dev_rg="rg-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

dev_url=$(az containerapp show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --query properties.latestRevisionFqdn -o tsv 2>/dev/null || echo "pending")

if [[ "$dev_url" != "pending" ]]; then
  curl -s "https://$dev_url/health/live" | jq .
  curl -s "https://$dev_url/health/ready" | jq .
fi
```

Expected: health endpoints return 200, service reports ready status.

**Manual approval gate**: Review dev logs and metrics before promoting to production.

```bash
az containerapp logs show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --since 30m | tail -20
```

### Stage 11: Manual Migration Job (Pre-Deployment)

Before deploying any new code revision, run the manual migration job to apply schema changes:

```bash
# Trigger migration job via Azure portal or CLI
migration_job_name="containerappsjob-messagebridge-migration-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

# Manual trigger (no new migration job instance needed unless schema changed)
az containerapp job start \
  --resource-group "$dev_rg" \
  --name "$migration_job_name"

# Monitor job execution
az containerapp job execution list \
  --resource-group "$dev_rg" \
  --name "$migration_job_name" \
  --query "[0].properties.status" -o tsv
```

Expected output: `Succeeded` (or `Running` if still executing).

**Important**: Migration runs independently before the worker starts. If migration fails, the worker remains unchanged and deployable once migration is fixed. See [Migration Failure Runbook](./migration-failure.md) for recovery.

### Stage 12: First Release (Production)

Promote image to production after dev validation:

```bash
# Tag image for production
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.0 \
  --push .

# Get production digest
prod_worker_digest="$(
  gh api repos/chanakya-net/whatsapp-messaging/packages \
    --jq '.[] | select(.name | contains("worker")) | .latest_version.docker_image_metadata.layers[0].digest'
)"
```

Deploy to production using delivery workflow:

```bash
# Trigger production deployment via GitHub Actions
gh workflow run delivery.yml \
  -f target=prod \
  -f environment=none

# Monitor deployment progress
gh run list --workflow=delivery.yml --limit=1
```

Verify production deployment:

```bash
# Get production Container App URL
prod_ca_name="ca-messagebridge-prod-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
prod_rg="rg-messagebridge-prod-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

prod_url=$(az containerapp show \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --query properties.latestRevisionFqdn -o tsv 2>/dev/null || echo "pending")

if [[ "$prod_url" != "pending" ]]; then
  curl -s "https://$prod_url/health/live" | jq .
  curl -s "https://$prod_url/health/ready" | jq .
fi
```

Expected: health endpoints return 200, service reports ready status.

## Ongoing Operations

See related runbooks for operational procedures:

- [Rollback Runbook](./rollback.md) — Worker version rollback
- [Migration Failure Runbook](./migration-failure.md) — Database schema recovery
- [Secret Rotation Runbook](./secret-rotation.md) — Key Vault secret updates
- [CloudAMQP Outage Runbook](./cloudamqp-outage.md) — Messaging broker recovery
- [Database Restore Runbook](./database-restore.md) — Point-in-time recovery

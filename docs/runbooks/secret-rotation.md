# Secret Rotation Runbook

Procedure for rotating credentials stored in Azure Key Vault and reloading them in running worker instances.

## Overview

MessageBridge stores all secrets in Azure Key Vault:
- Database password
- RabbitMQ password
- New Relic license key
- API keys

Secrets are versioned in Key Vault. When a secret expires or is compromised, a new version is created, and workers must reload it.

**Key principle:** Workers fetch secrets at startup and cache them. Rotating a secret does not automatically reload it in running instances; you must restart the worker or trigger a configuration reload.

## Prerequisites

- Azure CLI (`az`) authenticated with Key Vault Administrator role
- Access to the target Key Vault (`kv-msgbr-prod-cin-042` or equivalent)
- At least one running worker instance (for testing reload)
- New secret value provided out of band (never created in this runbook)

## Rotation Strategy

Rotate secrets in this order to minimize downtime:

1. **Dev first** — Create new version in dev Key Vault, test worker reload
2. **Staging** (if applicable) — Repeat in staging environment
3. **Production** — Create new version in prod, coordinate worker restart
4. **Cleanup** — Verify old versions are no longer in use, optionally disable them

## Step 1: Create New Secret Version in Dev

```bash
# Connect to dev Key Vault
kv_name="kv-msgbr-dev-cin-042"
secret_name="messagebridge-rabbitmq-password"  # example

# Obtain new password (provided out of band)
new_password=$(read -sp "Enter new $secret_name: "; echo "$REPLY")

# Create new version in Key Vault
az keyvault secret set \
  --vault-name "$kv_name" \
  --name "$secret_name" \
  --value "$new_password"

# Verify new version was created (should show new date)
az keyvault secret show \
  --vault-name "$kv_name" \
  --name "$secret_name" \
  --query '[version,attributes.created]' -o json
```

Expected output:

```json
[
  "abc123def456...",
  "2026-08-16T15:30:00Z"
]
```

Record the version ID (first line).

## Step 2: Test Reload in Dev

Deploy worker pointing to new secret version and verify it loads correctly:

```bash
# Get dev Container App name
dev_ca_name="ca-messagebridge-dev-cin-042"
dev_rg="rg-messagebridge-dev-cin-042"

# Check current configuration (verify it's using old version)
az containerapp show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --query properties.template.containers[0].env | jq '.[] | select(.name=="RabbitMq__Password")'

# Manually restart the Container App (this forces workers to fetch latest secret)
az containerapp update \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --image "$(az containerapp show --resource-group "$dev_rg" --name "$dev_ca_name" --query properties.template.containers[0].image -o tsv)"

# Wait for restart (30-60 seconds)
sleep 60

# Verify worker health
dev_url=$(az containerapp show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$dev_url/health/ready" | jq .
```

Expected: health endpoint returns 200, worker connects to RabbitMQ successfully.

### Failure Handling

If worker fails to start or health check fails after restart:

```bash
# Check worker logs for credential errors
az containerapp logs show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --since 10m | grep -i "password\|auth\|connection"

# Verify the secret value in Key Vault
az keyvault secret show \
  --vault-name "$kv_name" \
  --name "$secret_name" \
  --query value

# If secret is wrong, create another new version with correct value
# Do NOT re-use incorrect version
```

## Step 3: Production Approval and Rotation

After successful dev test, apply to production.

**Approval step:** Notify team and get sign-off before rotating prod secrets. Document who approved and timestamp.

```bash
# Rotate in production
prod_kv="kv-msgbr-prod-cin-042"
prod_ca_name="ca-messagebridge-prod-cin-042"
prod_rg="rg-messagebridge-prod-cin-042"

# Create new version (same secret value as dev test)
az keyvault secret set \
  --vault-name "$prod_kv" \
  --name "$secret_name" \
  --value "$new_password"

# Verify version created
az keyvault secret show \
  --vault-name "$prod_kv" \
  --name "$secret_name" \
  --query '[version,attributes.created]' -o json

# Restart production worker(s)
# This minimizes downtime by using Container Apps revision swap
prod_image=$(az containerapp show \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --query properties.template.containers[0].image -o tsv)

az containerapp update \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --image "$prod_image"

# Monitor health during restart (60–90 seconds)
prod_url=$(az containerapp show \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

# Polling loop: check health every 5 seconds for 2 minutes
for i in {1..24}; do
  echo "[$i] Checking health..."
  curl -s "https://$prod_url/health/ready" | jq .
  sleep 5
done
```

## Step 4: Verify Production Connectivity

After restart, verify worker is using new credentials:

```bash
# Check RabbitMQ connectivity
rabbitmq_host=$(echo "$prod_url" | sed 's/^.*--//; s/.azurecontainerapps.io.*//')
az containerapp exec \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  -- bash -c "echo 'AMQP connection established' | tee /proc/self/fd/2"

# Check logs for successful authentication
az containerapp logs show \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --since 5m | grep -E "connected|authenticated|startup"
```

Expected: logs show successful connection to RabbitMQ/PostgreSQL without authentication errors.

## Step 5: Cleanup Old Versions

After confirming new secret is working, optionally disable old versions to prevent accidental use:

```bash
# List all versions of the secret
az keyvault secret list-versions \
  --vault-name "$prod_kv" \
  --name "$secret_name" \
  --query '[].id' -o tsv

# Disable old versions (keep last 2 for rollback)
# Get the list of version IDs, then disable older ones:

az keyvault secret update \
  --vault-name "$prod_kv" \
  --name "$secret_name" \
  --id "https://${prod_kv}.vault.azure.net/secrets/$secret_name/OLD_VERSION_ID" \
  --set attributes.enabled=false
```

## Expected Results

- New secret version created in Key Vault
- Workers restart and fetch new secret
- Application remains available (zero downtime via Container Apps revision swap)
- All services (RabbitMQ, PostgreSQL, etc.) accept new credentials
- Old versions are disabled or documented in runbook

## Failure Interpretation

| Symptom | Cause | Action |
|---------|-------|--------|
| Worker fails to start after restart | Incorrect new secret value | Roll back to previous version; verify secret value |
| Partial restarts: some workers use old credential | Deployment in progress; revision swap incomplete | Wait 90 seconds; check all replicas are on new revision |
| `authentication failed` errors in logs | Credential value typo or Key Vault permission issue | Verify secret value and managed identity permissions |
| RabbitMQ connection drops after restart | Broker doesn't recognize new credential | Verify credential is configured in broker first (out-of-band) |
| Long downtime during restart | Container App restart is slow (rare) | Consider rolling restart instead of full restart |

## Rolling Restart (Alternative for Zero-Downtime)

For critical production systems, rotate with zero downtime using Container Apps slots:

```bash
# Create a staging slot with new configuration
az containerapp revision label create \
  --name "$prod_ca_name" \
  --resource-group "$prod_rg" \
  --label staging \
  --no-prompt

# Verify staging slot is healthy
sleep 60
curl -s "https://${prod_ca_name}--staging.azurecontainerapps.io/health/ready"

# Swap traffic 10% to staging
az containerapp traffic set \
  --name "$prod_ca_name" \
  --resource-group "$prod_rg" \
  --traffic staging=10 production=90

# Monitor errors for 5 minutes
sleep 300

# If no errors, swap 100%
az containerapp traffic set \
  --name "$prod_ca_name" \
  --resource-group "$prod_rg" \
  --traffic staging=100

# Clean up production slot
az containerapp update \
  --name "$prod_ca_name" \
  --resource-group "$prod_rg" \
  --remove labels production || true
```

## Post-Rotation Documentation

Log the rotation in your operational runbook or incident tracker:

```
Date: 2026-08-16
Secret: messagebridge-rabbitmq-password
Old Version: abc123def456...
New Version: xyz789abc123...
Approved by: [team member]
Dev tested: yes (2026-08-16 15:30 UTC)
Prod rotated: yes (2026-08-16 16:00 UTC)
Status: complete, workers healthy
```

## Scheduled Rotations

Key secrets should be rotated on a schedule:

- **Database password** — quarterly or per security policy
- **RabbitMQ password** — quarterly or per security policy
- **API keys** — annually or per security policy
- **New Relic license key** — per New Relic renewal

Set calendar reminders 2 weeks before scheduled rotation to plan and notify stakeholders.

## See Also

- [Deployment Guide](./deployment.md)
- [Operations Guide](../operations.md)
- [Key Vault Policy Contract Tests](../../.github/scripts/tests/key-vault-policy.test.sh)

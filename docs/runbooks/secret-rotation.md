# Secret Rotation Runbook

Procedure for rotating credentials stored in Azure Key Vault and reloading them in running worker instances.

## Overview

MessageBridge stores all secrets in Azure Key Vault using versionless references:

- `rabbitmq-connection-string` — CloudAMQP connection credentials
- `new-relic-otlp-headers` — New Relic authentication headers
- `whatsapp-provider-placeholder` — disabled placeholder
- `email-provider-placeholder` — disabled placeholder

**Key principle**: Workers fetch secrets at startup and cache them. Versionless references automatically get the latest version. Creating a new secret version does not restart workers; you must trigger a reload job.

**Security critical**: Never retrieve or display secret values via CLI, logs, or terminal history. Use Azure Key Vault portal for all value changes.

## Prerequisites

- Azure CLI (`az`) authenticated with Key Vault Administrator role
- Access to target Key Vault via Azure portal (kv-msgbr-{dev|prod}-{region}-{serial})
- At least one running worker instance (for testing reload)
- New secret value provided out of band (never created in this runbook)

## Rotation Strategy

Rotate secrets in this order to minimize downtime:

1. **Dev first** — Create new version in dev Key Vault (portal), test worker reload
2. **Production** — Create new version in prod Key Vault (portal), trigger reload via delivery.yml
3. **Cleanup** — Verify old versions are disabled/archived in Key Vault (optional)

## Step 1: Create New Secret Version (Dev)

**Via Azure Portal** (recommended):

1. Navigate to Key Vault: kv-msgbr-dev-{region}-{serial}
2. Select Secrets from left menu
3. Click the secret name (e.g., `rabbitmq-connection-string`)
4. Click "+ New Version"
5. Paste the new value (provided out of band, never in this runbook)
6. Click Create
7. Copy the version URI (for verification)

**Never** use CLI to set secrets in runbooks. Azure Portal ensures values are entered securely without terminal history or logs.

Expected output: new version created with timestamp and URI.

## Step 2: Test Reload in Dev

Trigger the protected reload workflow to reload secrets in running worker instances:

```bash
# Trigger secret reload via delivery.yml
gh workflow run delivery.yml \
  -f target=reload-secrets \
  -f environment=dev

# Monitor reload progress
gh run list --workflow=delivery.yml --limit=1 --json status,conclusion
```

This workflow:
- Signals all dev workers to reload from Key Vault
- Each worker fetches the versionless reference again (gets latest version)
- No pod/container restart required

Check worker health after reload:

```bash
# Get dev Container App URL
dev_ca_name="ca-messagebridge-dev-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
dev_rg="rg-messagebridge-dev-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

dev_url=$(az containerapp show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

# Verify worker is ready
curl -s "https://$dev_url/health/ready" | jq .
```

Expected: health endpoint returns 200, worker reports ready.

### Failure Handling

If reload fails:

```bash
# Check worker logs for credential errors
az containerapp logs show \
  --resource-group "$dev_rg" \
  --name "$dev_ca_name" \
  --since 10m | grep -i "error\|connection"

# Verify the secret exists and is readable (portal only)
# Do NOT query the secret value via CLI
```

If secret is wrong, create another new version in Key Vault portal with the correct value. Do not re-use an incorrect version.

## Step 3: Production Approval and Rotation

After successful dev test, apply to production.

**Approval step**: Notify team and document who approved and when before rotating prod secrets.

Create new secret version in production Key Vault:

1. Navigate to Key Vault: kv-msgbr-prod-{region}-{serial}
2. Select Secrets from left menu
3. Click the secret name
4. Click "+ New Version"
5. Paste the new value (same value tested in dev)
6. Click Create

Trigger production reload:

```bash
# Trigger secret reload via delivery.yml
gh workflow run delivery.yml \
  -f target=reload-secrets \
  -f environment=prod

# Monitor reload progress
gh run list --workflow=delivery.yml --limit=1 --json status,conclusion
```

This workflow:
- Gracefully reloads secrets in production workers
- No downtime or revision swap required
- Workers fetch latest version from versionless URI

Verify production health:

```bash
# Get production Container App URL
prod_ca_name="ca-messagebridge-prod-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
prod_rg="rg-messagebridge-prod-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

prod_url=$(az containerapp show \
  --resource-group "$prod_rg" \
  --name "$prod_ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

# Verify worker is ready
curl -s "https://$prod_url/health/ready" | jq .
```

Expected: health endpoint returns 200, worker reports ready.

## Step 4: Cleanup (Optional)

After confirming the new secret version is working, disable the old version in Key Vault:

1. Navigate to Key Vault: kv-msgbr-prod-{region}-{serial}
2. Select Secrets
3. Click the secret name
4. Click on the old version
5. Click Disable (this marks it as inactive but preserves audit history)

Do not delete old versions; disabled versions remain in audit logs for compliance.

## Failure Recovery

If a secret is wrong and workers cannot reload:

1. **Create corrected version** in Key Vault portal with correct value
2. **Re-trigger reload**: `gh workflow run delivery.yml -f target=reload-secrets -f environment={dev|prod}`
3. **Verify health** again

If a secret was disclosed, immediately disable all versions and create a new one with a different value.

## Related Runbooks

- [Secret Rotation in CloudAMQP Outage](./cloudamqp-outage.md) — rotating credentials during broker failover
- [Deployment Runbook](./deployment.md) — initial secret seeding and bootstrap
- [Operations Guide](../operations.md) — ongoing system health and alerts

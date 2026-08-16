# Worker Rollback Runbook

Procedure for rolling back a worker container image in production when a release is discovered to have critical issues.

## Overview

Worker rollback is a container image deployment operation only — it does not touch database schema, configuration, or messaging state. The worker uses Azure Container Apps with revision history; rolling back is a matter of targeting a previous healthy image revision.

## Prerequisites

- Azure CLI (`az`) authenticated with contributor permissions to the target resource group
- Identify the broken container app name (`ca-messagebridge-prod-<region>-<serial>`)
- Know or locate the previous working revision (see Revision History section)

## Failure Interpretation

| Symptom | Meaning | Action |
|---------|---------|--------|
| `health/ready` endpoint returns 503 or timeout | Worker is unhealthy, may indicate broken code or startup sequence | Proceed with rollback |
| Application logs show repeated errors in startup or message processing | Code issue affecting all requests | Proceed with rollback |
| Only specific request patterns fail (e.g., one tenant) | May not be a code issue; investigate data state first before rolling back | Check application logs and database state |
| Container repeatedly crashes and restarts | Likely startup failure; rollback is correct action | Proceed with rollback |
| All replicas healthy but message throughput dropped 90%+ | May indicate performance regression or messaging issue; check CloudAMQP status first | Check broker and database before rollback |

## Recovery Boundaries

**What rollback can fix:**
- Application code bugs (startup, request handling, library incompatibilities)
- Dependency version incompatibilities
- Configuration syntax errors

**What rollback cannot fix:**
- Database schema corruption (requires [Migration Failure Runbook](./migration-failure.md))
- Message queue corruption (requires [CloudAMQP Outage Runbook](./cloudamqp-outage.md))
- Data consistency issues (requires investigation and possible PITR)

## Revision History

Locate the previous healthy revision:

```bash
# List all Container App revisions, most recent first
ca_name="ca-messagebridge-prod-cin-042"  # adjust serial/region as needed
ca_rg="rg-messagebridge-prod-cin-042"

az containerapp revision list \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --query '[0:10].[name,properties.template.containers[0].image,createdTime,active]' \
  -o table
```

Expected output:

```text
Name                                  Image                                             CreatedTime          Active
------------------------------------  ------------------------------------------------  -------------------  ------
ca-messagebridge-prod-cin-042--abc123  ghcr.io/.../worker:v1.0.1@sha256:dead...        2026-08-16T12:00:00Z True
ca-messagebridge-prod-cin-042--xyz789  ghcr.io/.../worker:v1.0.0@sha256:beef...        2026-08-16T11:00:00Z False
ca-messagebridge-prod-cin-042--qwe456  ghcr.io/.../worker:v0.9.9@sha256:cafe...        2026-08-16T10:00:00Z False
```

Identify the last known-good revision by checking:
1. Was it running stably for hours/days?
2. Do logs show no errors during that period?
3. Confirm the image digest matches a released version

## Rollback Procedure

### Step 1: Verify Current State

Confirm the current revision is broken:

```bash
# Check current revision status
az containerapp revision show \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --revision "$(az containerapp revision list --resource-group "$ca_rg" --name "$ca_name" --query '[0].name' -o tsv)" \
  -o json | jq '.properties | {image: .template.containers[0].image, replicas: .replicas, active: .active}'

# Check health endpoint
app_url=$(az containerapp show \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$app_url/health/ready" || echo "Health check failed"
```

### Step 2: Activate Previous Revision

Restore traffic to a known-good previous revision:

```bash
# Target the previous healthy revision
previous_revision="ca-messagebridge-prod-cin-042--xyz789"  # from revision history

# Activate the previous revision (100% traffic)
az containerapp revision activate \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --revision "$previous_revision"
```

Wait 30 seconds for traffic switch to complete.

### Step 3: Verify Rollback Success

Confirm the previous revision is now handling traffic:

```bash
# Wait for revision to stabilize
sleep 30

# Check that previous revision is now active
az containerapp revision list \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --query '[0:2].[name,active]' \
  -o table

# Verify health endpoints respond
curl -s "https://$app_url/health/live" | jq .
curl -s "https://$app_url/health/ready" | jq .

# Check for errors in logs
az containerapp logs show \
  --resource-group "$ca_rg" \
  --name "$ca_name" \
  --since 5m | tail -20
```

Expected: previous revision is now `active: True`, health endpoints return 200, no new errors in logs.

## Expected Result

- All replicas running the previous image revision
- `health/live` and `health/ready` both responding with 200
- Message throughput returning to baseline
- No error spikes in application logs or New Relic

## Cleanup

No cleanup required. The broken revision remains in revision history for analysis. Azure Container Apps retains the last 100 revisions.

To retain broken revision for investigation, leave it inactive. Container Apps charges only for active revisions.

## Post-Rollback Actions

1. **Investigate root cause** of broken release — check commit log, test results, deployment differences
2. **Create fix** in a new branch and validate thoroughly in dev before re-release
3. **Document incident** in your operational runbook or issue tracker
4. **Notify stakeholders** that service is restored and root cause is under investigation

## Escalation

If rollback does not restore health:

- Health endpoints still fail → [Database Restore Runbook](./database-restore.md) or [Migration Failure Runbook](./migration-failure.md)
- Message queue backlog growing → [CloudAMQP Outage Runbook](./cloudamqp-outage.md)
- Intermittent failures after rollback → investigate data consistency in PostgreSQL

## See Also

- [Deployment Guide](./deployment.md)
- [Migration Failure Runbook](./migration-failure.md)
- [Operations Guide](../operations.md)

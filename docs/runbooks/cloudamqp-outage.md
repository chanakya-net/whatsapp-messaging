# CloudAMQP Outage Runbook

Procedure for diagnosing and recovering from CloudAMQP messaging broker issues that impact MessageBridge worker communication.

## Overview

MessageBridge uses CloudAMQP (RabbitMQ-as-a-service) for asynchronous message publishing and consumption. An outage in CloudAMQP prevents workers from sending messages to the broker, causing:

- Message publishing failures
- Worker health check failures (if readiness probe requires broker connectivity)
- Message queue backlog
- Potential data loss if outbox cleanup is enabled

This runbook covers diagnosis, failover coordination, and recovery. Credential rotation is handled separately.

## Prerequisites

- Access to CloudAMQP web dashboard (credentials provided out of band)
- Azure CLI (`az`) for checking Azure resources
- `curl` for API testing
- Read-only database access for outbox inspection (optional)

## Failure Diagnosis

### Step 1: Verify Broker Connectivity

```bash
# Test DNS resolution
nslookup instance-id.cloudamqp.com
# or
dig instance-id.cloudamqp.com

# Test TCP connectivity to broker port (5671 for TLS)
nc -zv instance-id.cloudamqp.com 5671

# Check worker logs for connectivity errors
az containerapp logs show \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --since 10m | grep -i "rabbitmq\|amqp\|connection"
```

Expected output if broker is reachable: connection established, no timeouts.

### Step 2: Check CloudAMQP Dashboard

Log in to CloudAMQP web interface:

1. Visit https://customer.cloudamqp.com
2. Select your MessageBridge instance
3. Check **Status** tab:
   - All nodes show "running"
   - Message queue depth is normal (no unexplained backlog)
   - Connection count matches expected worker replicas
4. Check **Logs** tab for any error messages in the last hour

### Step 3: Check Azure Network Connectivity

```bash
# Verify Network Security Group allows outbound AMQP
az network nsg show \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "nsg-messagebridge-prod-cin-042" \
  --query 'securityRules[?direction==`Outbound`].{name:name,protocol:protocol,destination:destinationAddressPrefix,port:destinationPortRange}' \
  -o table

# Verify Container App can reach external services
ca_name="ca-messagebridge-prod-cin-042"
az containerapp exec \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "$ca_name" \
  -- curl -v instance-id.cloudamqp.com:5671 2>&1 | head -20
```

Expected: NSG has outbound rule allowing port 5671 to internet; curl shows connection attempt.

## Failure Interpretation

| Symptom | Cause | Action |
|---------|-------|--------|
| Worker logs: `connection refused` to instance-id.cloudamqp.com:5671 | CloudAMQP broker is down or unavailable | Check CloudAMQP dashboard status; contact CloudAMQP support if down |
| Worker logs: `connection timeout` after 30 seconds | Network routing issue or broker very slow | Check Azure NSG rules; test DNS resolution |
| Worker logs: `authentication failed` with stored credentials | Credential mismatch or broker vhost issue | Verify secret name exists in Key Vault; check broker vhost configuration |
| Worker logs: `queue does not exist` or `exchange not found` | Broker configuration mismatch or reset | Reconfigure exchanges/queues via CloudAMQP dashboard |
| Message backlog growing but no errors | Broker accepting connections but not processing | Check CloudAMQP memory/CPU utilization; check for dead-letter queue items |
| Worker health check fails on `/health/ready` | Readiness probe checks broker connectivity and it fails | Wait for broker recovery; broker connectivity is a hard dependency |
| Intermittent `connection reset` errors | Broker is unstable, crashing, or restarting | Wait for broker recovery; check CloudAMQP logs |

## Recovery Steps

### For CloudAMQP Service Outage (Broker Down)

If CloudAMQP status page shows an outage:

1. **Monitor status page** — https://status.cloudamqp.com (check for maintenance or incidents)
2. **Wait for broker recovery** — CloudAMQP team is investigating
3. **Monitor worker logs** — workers will automatically reconnect when broker comes back online
4. **Do NOT rotate credentials** yet; wait for recovery confirmation

Expected recovery time: CloudAMQP typically resolves outages within 15–60 minutes.

### For Credential Issues

If logs show `authentication failed`:

1. **Verify secret name** exists in Key Vault: `rabbitmq-connection-string`
   - Navigate to Key Vault → Secrets → check `rabbitmq-connection-string` exists
   - Do NOT query the value via CLI

2. **Verify secret is accessible** to workload identity:
   - Check role assignments on the Key Vault
   - Confirm runtime identity has "Key Vault Secrets User" role

3. **If credential is wrong**, see [Secret Rotation Runbook](./secret-rotation.md) for safe update procedures

### For Vhost Configuration Issues

CloudAMQP broker may have been reset or reconfigured:

1. Log into CloudAMQP dashboard
2. Navigate to Admin > Vhosts and Exchanges
3. Verify the following exist:
   - Virtual host: `/` (default)
   - Exchanges:
     - `messagebridge.outbox` (type: direct)
     - `messagebridge.worker` (type: fanout)
   - Queues:
     - `messagebridge.processing`
     - `messagebridge.outbox.deadletter` (if using DLQ)

If missing, recreate via infrastructure code or CloudAMQP dashboard.

### For Message Backlog Buildup

If broker is up but messages are not being consumed:

1. **Check CloudAMQP dashboard** for queue backlogs:
   - Navigate to Queues tab
   - Look for high message counts in messagebridge queues
   - Check for dead-letter queue buildup

2. **Inspect broker logs** (via CloudAMQP Admin > Logs):
   - Look for consumer disconnect events
   - Look for message nack patterns

3. **If backlog is large and broker is healthy**, restart workers to retry:

```bash
# Restart workers (causes revision swap, preserves image)
az containerapp update \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --image "$(az containerapp show \
    --resource-group 'rg-messagebridge-prod-cin-042' \
    --name 'ca-messagebridge-prod-cin-042' \
    --query properties.template.containers[0].image -o tsv)"
```

## Credential Rotation

If broker credentials were exposed or need routine rotation:

1. **Create new user** in CloudAMQP dashboard:
   - Admin > Users > Add User
   - New username: `messagebridge-<date>` (e.g., `messagebridge-20260816`)
   - New password: generate securely (out of band)

2. **Configure permissions** for new user:
   - Navigate to Admin > Permissions
   - Grant read, write, configure on virtual host `/`

3. **Update Key Vault** (via Azure portal, not CLI):
   - Navigate to Key Vault → Secrets → `rabbitmq-connection-string`
   - Click "+ New Version"
   - Paste new connection URI with new credentials
   - Click Create

4. **Reload workers** to pick up new credentials:

```bash
gh workflow run delivery.yml \
  -f target=reload-secrets \
  -f environment=prod
```

5. **Verify worker health**:

```bash
prod_url=$(az containerapp show \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$prod_url/health/ready" | jq .
```

Expected: health endpoint returns 200, worker reports ready.

6. **Disable old user** in CloudAMQP (optional):
   - Admin > Users > select old user > disable
   - Keep for audit trail; do not delete

## Related Runbooks

- [Secret Rotation Runbook](./secret-rotation.md) — safe credential updates
- [Deployment Runbook](./deployment.md) — initial CloudAMQP setup
- [Operations Guide](../operations.md) — ongoing system health and alerts

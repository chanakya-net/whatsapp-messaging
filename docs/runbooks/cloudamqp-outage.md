# CloudAMQP Outage Runbook

Procedure for diagnosing and recovering from CloudAMQP messaging broker issues that impact MessageBridge worker communication.

## Overview

MessageBridge uses CloudAMQP (RabbitMQ-as-a-service) for asynchronous message publishing and consumption. An outage in CloudAMQP prevents workers from sending messages to the broker, causing:

- Message publishing failures
- Worker health check failures (if readiness probe requires broker connectivity)
- Message queue backlog
- Potential data loss if outbox cleanup is enabled

This runbook covers diagnosis, failover coordination, credential rotation, and recovery.

## Prerequisites

- Access to CloudAMQP web dashboard (credentials provided out of band)
- Azure CLI (`az`) for checking Azure resources
- `curl` for API testing
- PostgreSQL client (`psql`) for checking outbox state

## Failure Diagnosis

### Step 1: Verify Broker Connectivity

```bash
# Test DNS resolution
nslookup instance-id.cloudamqp.com
# or
dig instance-id.cloudamqp.com

# Test TCP connectivity to broker port (5671 for TLS)
nc -zv instance-id.cloudamqp.com 5671
# or with curl (AMQP doesn't respond to HTTP, but connection attempt will show)

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
| Worker logs: `authentication failed` with valid credentials | Credential mismatch or broker vhost issue | Verify credentials in Key Vault; check broker vhost configuration |
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

If logs show `authentication failed` with valid credentials in Key Vault:

```bash
# Step 1: Verify credential in Key Vault
kv_name="kv-msgbr-prod-cin-042"
az keyvault secret show \
  --vault-name "$kv_name" \
  --name messagebridge-rabbitmq-password \
  --query value -o tsv

# Step 2: Verify credential matches CloudAMQP broker
# Log into CloudAMQP dashboard > Admin > Users
# Confirm username and password match

# Step 3: If credential is wrong, rotate via Secret Rotation Runbook
# See: docs/runbooks/secret-rotation.md
```

### For Vhost Configuration Issues

CloudAMQP broker may have been reset or reconfigured:

```bash
# Step 1: Log into CloudAMQP dashboard
# Step 2: Navigate to Admin > Vhosts and Exchanges
# Step 3: Verify the following exist:
#   - Virtual host: "/" (default) or your configured vhost
#   - Exchanges:
#     - messagebridge.outbox (type: direct)
#     - messagebridge.worker (type: fanout)
#   - Queues:
#     - messagebridge.processing
#     - messagebridge.outbox.deadletter (if using DLQ)

# Step 4: If missing, recreate via infrastructure code or dashboard
# This requires stopping workers to avoid connection errors during reconfiguration
```

### For Message Backlog Buildup

If broker is up but messages are not being consumed:

```bash
# Check outbox table for pending messages
db_name="psql-messagebridge-prod-cin-042"
db_password=$(az keyvault secret show \
  --vault-name "$kv_name" \
  --name messagebridge-db-password \
  --query value -o tsv)

psql \
  --host="${db_name}.postgres.database.azure.com" \
  --port=5432 \
  --username="dbadmin@${db_name}" \
  --dbname=messagebridge \
  --set PGPASSWORD="$db_password" \
  <<'SQL'
-- Check pending messages in outbox
SELECT COUNT(*) as pending_count
FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NULL;

-- Check oldest pending message
SELECT Id, CreatedAtUtc, ExchangeName, RoutingKey
FROM MessageBridgeOutboxMessages
WHERE PublishedAtUtc IS NULL
ORDER BY CreatedAtUtc
LIMIT 1;
SQL

# Check for dead-letter queue buildup
# Log into CloudAMQP dashboard > Queues
# Look for amq.dlq or messagebridge.outbox.deadletter with high message count

# If backlog is large and broker is healthy, restart workers to retry
az containerapp update \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --image "$(az containerapp show --resource-group 'rg-messagebridge-prod-cin-042' --name 'ca-messagebridge-prod-cin-042' --query properties.template.containers[0].image -o tsv)"
```

## Vhost and Credential Rotation

If broker credentials were exposed or need routine rotation:

```bash
# Step 1: Create new user in CloudAMQP dashboard
# CloudAMQP Admin > Users > Add User
# New username: messagebridge-<date> (e.g., messagebridge-20260816)
# New password: generate securely

# Step 2: Configure permissions for new user
# Navigate to: Admin > Permissions
# Grant read, write, configure on: /

# Step 3: Update secret in Key Vault (see Secret Rotation Runbook)
az keyvault secret set \
  --vault-name "$kv_name" \
  --name messagebridge-rabbitmq-password \
  --value "<new-password>"

# Step 4: Restart workers to use new credential
az containerapp update \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --image "$(az containerapp show --resource-group 'rg-messagebridge-prod-cin-042' --name 'ca-messagebridge-prod-cin-042' --query properties.template.containers[0].image -o tsv)"

# Step 5: Disable old user in CloudAMQP
# CloudAMQP Admin > Users > Disable old user (do not delete yet)

# Step 6: Monitor for 24 hours; if no issues, delete old user
```

## Broker Recovery after Data Loss

If CloudAMQP experiences data loss and messages were dropped:

1. **Identify lost messages** — compare outbox table with CloudAMQP queue contents
2. **Replay from outbox** — application can replay pending messages from the outbox table
3. **Manual cleanup** — if outbox cleanup deleted published messages incorrectly, restore from backup

This is a rare scenario. If it occurs, escalate to the on-call database owner.

## Expected Results After Recovery

- Worker logs show no connection errors
- `/health/ready` returns 200 OK
- Message throughput returns to baseline
- Outbox pending message count decreases over time
- No repeated errors in New Relic

## Post-Incident Actions

1. **Root cause analysis** — if outage was not communicated by CloudAMQP, investigate why
2. **Failover planning** — consider secondary messaging broker or queue provider
3. **Alert configuration** — ensure alerting is configured for broker connectivity failures
4. **Runbook update** — if a new failure mode was discovered, update this runbook

## Escalation

- **CloudAMQP service outage** → Monitor status page; contact CloudAMQP support
- **Broker credential reset** → Rotate credentials per Secret Rotation Runbook
- **Persistent connection failures** → Check Azure NSG and network configuration
- **Message data loss** → Escalate to on-call database owner; may require point-in-time restore

## See Also

- [Deployment Guide](./deployment.md)
- [Secret Rotation Runbook](./secret-rotation.md)
- [Operations Guide](../operations.md)
- [RabbitMQ Preflight Contract Tests](../../.github/scripts/tests/rabbitmq-preflight-contract.test.sh)

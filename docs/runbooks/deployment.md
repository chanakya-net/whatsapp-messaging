# Bootstrap and Deployment Runbook

Complete human-in-the-loop bootstrap from empty Azure/GitHub setup through production-ready MessageBridge platform. All twelve HITL stages are documented here and in related runbooks.

## Prerequisites and Authentication

### Stage 1: Authentication

Ensure these tools are installed and authenticated before bootstrap:

- **Azure CLI** (`az`) logged in as a principal with subscription-level permissions
- **GitHub CLI** (`gh`) authenticated with repo access and GHCR token
- **OpenTofu** (`tofu`) v1.7+ for IaC deployment
- **PostgreSQL client** (`psql`) for database operations
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
- `AcrPush` (for GHCR equivalents)

## Input Collection and Naming

### Stage 2: Input Collection

Create a file `bootstrap.env` with these values (copy/paste-safe template):

```bash
# Environment identifier
ENVIRONMENT=dev  # or prod
SERIAL=042       # three-digit environment serial (001-999)
REGION=centralindia  # Azure region (e.g., eastus, australiaeast)

# Azure subscription
AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000
AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000

# GitHub GHCR
GHCR_USERNAME=chanakya-net
GHCR_REPO=whatsapp-messaging

# Application naming (auto-derived; do not edit)
RESOURCE_GROUP_PREFIX=rg-messagebridge
KEY_VAULT_PREFIX=kv-msgbr
DATABASE_PREFIX=psql-messagebridge
CONTAINER_APP_PREFIX=ca-messagebridge
```

Load environment:

```bash
set -a
source bootstrap.env
set +a
```

### Stage 3: Naming and Region Checks

Validate naming and resource availability before bootstrap:

```bash
# Check naming convention
[[ "$SERIAL" =~ ^[0-9]{3}$ ]] || \
  echo "ERROR: SERIAL must be 3 digits (001-999), got $SERIAL"

# Check region exists
az account list-locations --query "[?name=='$REGION']" || \
  echo "ERROR: Region not valid: $REGION"

# Check Key Vault name availability (3-24 alphanumeric lowercase)
kv_name="kv-msgbr-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
[[ ${#kv_name} -le 24 ]] || \
  echo "ERROR: Key Vault name too long: $kv_name"
[[ "$kv_name" =~ ^[a-z0-9-]+$ ]] || \
  echo "ERROR: Key Vault name invalid: $kv_name"

# Display derived names (review before continue)
echo "Resource Group: $RESOURCE_GROUP_PREFIX-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
echo "Key Vault: $kv_name"
echo "Database: psql-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
echo "Container App: ca-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
```

## Bootstrap Phase: Planning

### Stage 4: State and OIDC Setup

Before applying infrastructure, configure OpenTofu state backend and GitHub OIDC:

**Step 1: Create storage account for state**

```bash
state_rg="rg-messagebridge-state"
state_storage="stmsgbr${ENVIRONMENT}${SERIAL}"

az group create \
  --name "$state_rg" \
  --location "$REGION"

az storage account create \
  --resource-group "$state_rg" \
  --name "$state_storage" \
  --kind StorageV2 \
  --sku Standard_LRS
```

**Step 2: Create state container**

```bash
state_container="tofu-state"
az storage container create \
  --account-name "$state_storage" \
  --name "$state_container" \
  --auth-mode login
```

**Step 3: Configure GitHub OIDC provider**

```bash
# Get GitHub organization/repo info
github_org="chanakya-net"
github_repo="whatsapp-messaging"

# Create Azure AD app registration for OIDC
app_display_name="github-oidc-$ENVIRONMENT"
app_id=$(az ad app create \
  --display-name "$app_display_name" \
  --query appId -o tsv)

# Create service principal
principal_id=$(az ad sp create \
  --id "$app_id" \
  --query id -o tsv)

# Assign Contributor role
az role assignment create \
  --assignee "$principal_id" \
  --role Contributor \
  --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID"

# Configure OIDC
az ad app federated-credential create \
  --id "$app_id" \
  --parameters \
  "{
    \"name\": \"github-oidc-$ENVIRONMENT\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$github_org/$github_repo:ref:refs/heads/main\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"

printf 'GITHUB_OIDC_CLIENT_ID=%s\n' "$app_id"
printf 'GITHUB_OIDC_TENANT_ID=%s\n' "$AZURE_TENANT_ID"
```

Record output. Update GitHub repository secrets with `GITHUB_OIDC_CLIENT_ID` and `GITHUB_OIDC_TENANT_ID`.

### Stage 5: Foundation Apply

Initialize and apply foundational infrastructure (Key Vault, Storage, Networking):

```bash
cd infra/opentofu/foundation

# Initialize terraform backend
tofu init \
  -backend-config="resource_group_name=$state_rg" \
  -backend-config="storage_account_name=$state_storage" \
  -backend-config="container_name=$state_container" \
  -backend-config="key=$ENVIRONMENT-foundation.tfstate"

# Plan infrastructure changes
tofu plan \
  -var="environment=$ENVIRONMENT" \
  -var="serial=$SERIAL" \
  -var="region=$REGION" \
  -var="tenant_id=$AZURE_TENANT_ID" \
  -var="subscription_id=$AZURE_SUBSCRIPTION_ID" \
  -out=tfplan

# Review plan output carefully
# Expected: Key Vault, Storage, Virtual Network, Subnets

# Apply foundation
tofu apply tfplan
```

Expected output includes:
- Key Vault name and URI
- Storage account endpoints
- Virtual Network ID
- Database subnet ID

Record these outputs for later stages.

### Stage 6: Dummy Secret Seeding

Initialize Key Vault with placeholder secrets before workloads reference them:

```bash
# Bootstrap placeholder secrets (no real values required yet)
bash scripts/infra/seed-placeholder-secrets.sh \
  --vault-name "kv-msgbr-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
```

Expected secrets created:
- `messagebridge-db-password`
- `messagebridge-rabbitmq-password`
- `messagebridge-api-key`
- `new-relic-license-key`

Do not use these placeholder values in production. Real secret values arrive out of band from Key Vault.

## Workload Phase: Database and Messaging

### Stage 7: Database Grants and Initialization

Create PostgreSQL database and configure application permissions:

**Step 1: Create database**

```bash
db_name="psql-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"
db_rg="rg-messagebridge-${ENVIRONMENT}-${REGION:0:3}-${SERIAL}"

az postgres flexible-server create \
  --resource-group "$db_rg" \
  --name "$db_name" \
  --location "$REGION" \
  --admin-user dbadmin \
  --admin-password "$(az keyvault secret show --vault-name "$kv_name" --name messagebridge-db-password --query value -o tsv)" \
  --sku-name Standard_B1ms \
  --storage-size 32 \
  --version 15 \
  --tier Burstable \
  --yes
```

**Step 2: Configure firewall access**

```bash
# Create firewall rule for your operator IP
operator_ip=$(curl -s https://api.ipify.org)

az postgres flexible-server firewall-rule create \
  --resource-group "$db_rg" \
  --name "$db_name" \
  --rule-name operator-access \
  --start-ip-address "$operator_ip" \
  --end-ip-address "$operator_ip"
```

**Step 3: Initialize database schema**

```bash
# Get database connection string from Key Vault
db_password=$(az keyvault secret show --vault-name "$kv_name" --name messagebridge-db-password --query value -o tsv)

# Run migration using migration container image
docker run \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e "ConnectionStrings__DefaultConnection=Host=${db_name}.postgres.database.azure.com;Port=5432;Database=messagebridge;Username=dbadmin;Password=$db_password;SslMode=Require;" \
  ghcr.io/chanakya-net/whatsapp-messaging/migrate:latest
```

### Stage 8: CloudAMQP Configuration

Provision messaging broker and configure worker connectivity:

**Step 1: Create or configure CloudAMQP instance**

```bash
# Record CloudAMQP instance details (provided out of band)
# From CloudAMQP portal:
cloudamqp_host="instance-id.cloudamqp.com"
cloudamqp_vhost="/"
cloudamqp_user="user-id"

# Store credentials in Key Vault
az keyvault secret set \
  --vault-name "$kv_name" \
  --name messagebridge-rabbitmq-password \
  --value "$(read -sp 'CloudAMQP password: ' pass; echo "$pass")"
```

**Step 2: Configure queues and exchanges**

```bash
# Use CloudAMQP web dashboard to verify:
# - Virtual host "/" exists
# - Default user has all permissions
# - No transient queues left from previous deployments
```

See [CloudAMQP Outage Runbook](./cloudamqp-outage.md) for recovery procedures.

## Workload Phase: Application Deployment

### Stage 9: New Relic and Observability

Configure observability for the worker:

**Step 1: Create New Relic application**

```bash
# Store New Relic license key in Key Vault (provided out of band)
az keyvault secret set \
  --vault-name "$kv_name" \
  --name new-relic-license-key \
  --value "$(read -sp 'New Relic license key: ' key; echo "$key")"
```

**Step 2: Verify New Relic ingestion**

```bash
# After first worker deployment, check New Relic dashboard for:
# - Service entity created for MessageBridge.Worker
# - Transactions and throughput visible
# - Database and messaging metrics flowing
```

### Stage 10: Container Registry Visibility

Ensure worker image is publicly readable from GHCR:

```bash
# Check image access
gh api repos/chanakya-net/whatsapp-messaging/packages \
  --jq '.[] | select(.name=="ghcr.io/chanakya-net/whatsapp-messaging/worker")'

# If private, set to public via GitHub Settings > Packages > Package settings
# Or via CLI (if available): gh api repos/.../packages/... --input -X PATCH
```

### Stage 11: First Release (Dev)

Create and test first worker release in dev environment:

```bash
# Build and push worker image
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:dev-v1.0.0 \
  --push .

# Get published digest
worker_digest=$(gh api repos/chanakya-net/whatsapp-messaging/packages -q '.[] | select(.name=="ghcr.io/chanakya-net/whatsapp-messaging/worker") | .latest_version.docker_tags[0]')

# Deploy worker to dev Azure Container App
ca_name="ca-messagebridge-dev-${REGION:0:3}-${SERIAL}"
az containerapp create \
  --resource-group "$db_rg" \
  --name "$ca_name" \
  --image "ghcr.io/chanakya-net/whatsapp-messaging/worker@${worker_digest}" \
  --environment "${REGION:0:3}" \
  --env-vars ASPNETCORE_ENVIRONMENT=Development \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 0.5 \
  --memory 1Gi

# Wait for deployment to reach Ready
az containerapp show \
  --resource-group "$db_rg" \
  --name "$ca_name" \
  --query properties.provisioningState
```

Verify dev deployment:

```bash
# Health check
dev_url=$(az containerapp show \
  --resource-group "$db_rg" \
  --name "$ca_name" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$dev_url/health/live" | jq .
curl -s "https://$dev_url/health/ready" | jq .
```

### Stage 12: First Release (Production)

Promote worker image to production after dev validation:

```bash
# Tag image for production
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.0 \
  --push .

# Deploy to production Container App
ca_prod_name="ca-messagebridge-prod-${REGION:0:3}-${SERIAL}"
az containerapp create \
  --resource-group "$db_rg" \
  --name "$ca_prod_name" \
  --image "ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.0" \
  --environment "${REGION:0:3}" \
  --env-vars ASPNETCORE_ENVIRONMENT=Production \
  --min-replicas 1 \
  --max-replicas 3 \
  --cpu 0.5 \
  --memory 1Gi

# Enable autoscaling for production
az containerapp autoscale \
  --name "$ca_prod_name" \
  --resource-group "$db_rg" \
  --min-replicas 1 \
  --max-replicas 5 \
  --rules cpu=80 memory=80
```

Verify production deployment:

```bash
prod_url=$(az containerapp show \
  --resource-group "$db_rg" \
  --name "$ca_prod_name" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$prod_url/health/live" | jq .
curl -s "https://$prod_url/health/ready" | jq .
```

## Ongoing Operations

See related runbooks for operational procedures:

- [Rollback Runbook](./rollback.md) — Worker version rollback
- [Migration Failure Runbook](./migration-failure.md) — Database schema recovery
- [Secret Rotation Runbook](./secret-rotation.md) — Key Vault secret updates
- [CloudAMQP Outage Runbook](./cloudamqp-outage.md) — Messaging broker recovery
- [Database Restore Runbook](./database-restore.md) — Point-in-time restore drills

## See Also

- [Operations Guide](../operations.md)

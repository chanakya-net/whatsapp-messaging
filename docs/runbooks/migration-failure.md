# Migration Failure Runbook

Procedure for recovering from failed or corrupted database migrations. A migration failure prevents worker startup and requires manual intervention.

## Overview

MessageBridge uses EF Core migrations to manage PostgreSQL schema. A migration failure occurs when:

1. A new migration is deployed but fails during `Database.Migrate()` at worker startup
2. The migration partially succeeds, leaving schema in an inconsistent state
3. A previously successful migration is reverted without a proper "down" migration

Migration failures block worker startup. Unlike application code bugs (which can be fixed with [Rollback Runbook](./rollback.md)), migration failures require either:

- A **forward-fix migration** (write a new migration to recover the schema)
- A **point-in-time restore** (roll back the database to before the bad migration)

## Recovery Boundaries

**Worker rollback does NOT fix migration failures.**

The worker uses `Database.Migrate()` at startup. Deploying an old worker image still runs any new migrations that were already applied to the database. You must fix the database state first.

**Options:**

1. **Forward-fix** — write a new EF Core migration that corrects the schema issue (fastest, preferred)
2. **Point-in-time restore** — restore the entire database to a point before the migration was applied (slower, requires downtime)

## Failure Interpretation

| Symptom | Cause | Action |
|---------|-------|--------|
| Worker logs: `Entity type 'X' has no key defined` | Missing or incorrect primary key definition in migration | Write forward-fix migration |
| Worker logs: `duplicate key value violates unique constraint` | Migration created constraint that data violates | Write forward-fix migration to fix data or relax constraint |
| Worker logs: `column 'X' does not exist` | Migration didn't create expected column or created with wrong type | Write forward-fix migration to add/fix column |
| Worker logs: `relation 'X' does not exist` | Migration didn't create table or dropped existing table | Write forward-fix migration to restore or create table |
| Migration succeeded in dev but failed in prod with different error | Environment-specific data issue (nullable violation, existing duplicates, etc.) | Inspect prod data, write forward-fix to handle it |
| Cannot determine failure cause from logs | Severe corruption or unrelated database issue | Escalate; consider point-in-time restore |

## Forward-Fix Migration

Create a new migration that corrects the broken schema without rolling back.

### Step 1: Identify the Failed Migration

```bash
# Connect to production database
db_name="psql-messagebridge-prod-cin-042"
db_password=$(az keyvault secret show \
  --vault-name "kv-msgbr-prod-cin-042" \
  --name messagebridge-db-password \
  --query value -o tsv)

psql \
  --host="${db_name}.postgres.database.azure.com" \
  --port=5432 \
  --username="dbadmin@${db_name}" \
  --dbname=messagebridge \
  --set PGPASSWORD="$db_password" \
  <<'SQL'
-- List all applied migrations
SELECT MigrationId, ProductVersion
FROM __EFMigrationsHistory
ORDER BY MigrationId DESC
LIMIT 5;
SQL
```

Record the most recent `MigrationId` and the one before it.

### Step 2: Analyze the Broken Migration

```bash
# Inspect the migration file
migration_file="src/MessageBridge.Infrastructure/Data/Migrations/$(date +%Y%m%d%H%M%S)_FixMigrationName.cs"

# Review the SQL in the migration that failed
# Check: columns created, constraints added, data transformations

# Run any data fixes needed before migration
psql \
  --host="${db_name}.postgres.database.azure.com" \
  --port=5432 \
  --username="dbadmin@${db_name}" \
  --dbname=messagebridge \
  --set PGPASSWORD="$db_password" \
  <<'SQL'
-- Example: if migration failed due to data constraints
-- Fix data state before reapplying migration

-- Check for data that violates constraint
SELECT * FROM YourTable WHERE nullable_column IS NULL;

-- Fix it
UPDATE YourTable SET nullable_column = 'default' WHERE nullable_column IS NULL;
SQL
```

### Step 3: Create Forward-Fix Migration

In Visual Studio or via `dotnet ef`:

```bash
cd src/MessageBridge.Infrastructure

# Create a new migration that corrects the broken one
dotnet ef migrations add FixPreviousMigrationName \
  --context MessageBridgeDbContext \
  --output-dir Data/Migrations

# Review the generated migration
cat Data/Migrations/$(ls -t Data/Migrations/*_FixPreviousMigrationName.cs | head -1)
```

Edit the generated migration to:

1. Remove any `SQL` statements that would re-apply the broken change
2. Add only the corrections needed
3. Verify column types, constraints, indexes match the intended schema

Example forward-fix migration:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // Add column that was missing in previous migration
    migrationBuilder.AddColumn<string>(
        name: "MissingColumn",
        table: "YourTable",
        type: "text",
        nullable: false,
        defaultValue: "");

    // Add index that previous migration didn't create
    migrationBuilder.CreateIndex(
        name: "IX_YourTable_MissingColumn",
        table: "YourTable",
        column: "MissingColumn");
}
```

### Step 4: Test Forward-Fix Locally

```bash
# Apply the forward-fix migration locally
dotnet ef database update \
  --context MessageBridgeDbContext

# Verify schema is correct
# Run unit tests
dotnet test
```

### Step 5: Deploy Forward-Fix

Commit the forward-fix migration:

```bash
git add src/MessageBridge.Infrastructure/Data/Migrations/*
git commit -m "fix: forward-fix migration to correct broken schema change"
git push
```

Build and deploy a new worker image that includes the forward-fix migration:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --file src/MessageBridge.Worker/Dockerfile \
  --tag ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.1-fix \
  --push .

# Deploy to Azure Container App
az containerapp update \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --image "ghcr.io/chanakya-net/whatsapp-messaging/worker:v1.0.1-fix"
```

Worker will start and apply the forward-fix migration on startup. Verify:

```bash
app_url=$(az containerapp show \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "ca-messagebridge-prod-cin-042" \
  --query properties.latestRevisionFqdn -o tsv)

curl -s "https://$app_url/health/ready"
```

## Point-in-Time Restore (Alternative)

If the forward-fix approach is not viable, restore the database to a point before the bad migration.

### Prerequisites

- Permission to create and destroy temporary PostgreSQL servers
- Knowledge of the approximate time the bad migration was applied
- A backup retention window that includes the target restore point (default: 7 days)

### Step 1: Determine Restore Point

```bash
# Estimate the time the bad migration started
# Typically visible in deployment or worker logs with timestamp

# Restore point should be 5–10 minutes before the migration was deployed
restore_time="2026-08-16T10:45:00Z"  # adjust to actual time

echo "Restore point: $restore_time"
echo "Loss of data after this time: ALL changes to the database"
```

### Step 2: Perform PITR Drill

Before touching production, validate the restore process with the quarterly drill:

```bash
bash docs/runbooks/database-restore.md
```

### Step 3: Restore Production Database

Create a temporary server, verify data integrity, then swap to production:

```bash
# This operation requires explicit decision and approval
# Step through the restore-drill.sh for prod database

export MESSAGEBRIDGE_RESTORE_SERIAL=042  # prod serial
export MESSAGEBRIDGE_RESTORE_POINTTIME="2026-08-16T10:45:00Z"

bash scripts/db/restore-drill.sh plan
# Review plan carefully

bash scripts/db/restore-drill.sh restore
# Wait for restore and verification to complete

# After verification, update connection strings and DNS to point to restored server
# (This requires Azure CLI and careful planning to avoid data loss)

az postgres flexible-server delete \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "psql-messagebridge-prod-cin-042"

az postgres flexible-server rename \
  --resource-group "rg-messagebridge-prod-cin-042" \
  --name "psql-messagebridge-drill-042-<timestamp>" \
  --new-name "psql-messagebridge-prod-cin-042"
```

**Downtime during PITR:** Approximately 1–2 hours (restore time + DNS propagation + worker restart).

## Expected Results

After successful migration recovery:

- Worker starts without errors
- `/health/ready` returns 200 OK
- Message throughput resumes
- New Relic shows worker running latest version
- No `__EFMigrationsHistory` errors in logs

## Post-Recovery Actions

1. **Root cause analysis** — why did the original migration fail? Was schema design flawed? Were there data constraints not anticipated?
2. **Test improvement** — add test cases to catch this scenario in the future
3. **Documentation** — update migration guidelines if needed
4. **Incident log** — record what went wrong and how it was recovered

## Escalation

If neither forward-fix nor PITR resolves the issue:

- Forward-fix creates new errors → escalate to database administrator; may indicate data corruption
- PITR fails repeatedly → escalate; may indicate backup/restore infrastructure issue
- Worker still won't start after recovery → [Rollback Runbook](./rollback.md) to previous version

## See Also

- [Deployment Guide](./deployment.md)
- [Rollback Runbook](./rollback.md)
- [Database Restore Runbook](./database-restore.md)
- [Operations Guide](../operations.md)

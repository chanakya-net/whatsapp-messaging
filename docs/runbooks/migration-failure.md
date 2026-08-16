# Migration Failure Runbook

Procedure for recovering from failed or corrupted database migrations. A migration failure prevents the worker from starting and requires manual intervention.

## Overview

MessageBridge uses a manual Container Apps migration job to apply EF Core migrations to PostgreSQL schema before the worker starts.

A migration failure occurs when:

1. A new migration is deployed but fails during the manual migration job execution
2. The migration partially succeeds, leaving schema in an inconsistent state
3. A previously successful migration is reverted without a proper forward-fix migration

**Critical distinction**: The worker does NOT run migrations at startup. Migrations are run independently via a manual Container Apps job, triggered before any worker deployment.

## Recovery Boundaries

**Migration failures do not block worker startup** because migrations are pre-deployment.

If a migration fails:
- The worker remains unchanged (no new revision deployed yet)
- The database schema is either unchanged or partially modified
- Recovery requires either a forward-fix migration or database restore

**Options:**

1. **Forward-fix** — write a new EF Core migration that corrects the schema issue (fastest, preferred)
2. **Point-in-time restore** — restore the entire database to a point before the failed migration (slower, requires downtime)

See [Database Restore Runbook](./database-restore.md) for point-in-time recovery procedures.

## Failure Interpretation

| Symptom | Cause | Action |
|---------|-------|--------|
| Job logs: `Entity type 'X' has no key defined` | Missing or incorrect primary key definition in migration | Write forward-fix migration |
| Job logs: `duplicate key value violates unique constraint` | Migration created constraint that existing data violates | Write forward-fix migration to fix data or relax constraint |
| Job logs: `column 'X' does not exist` | Migration didn't create expected column or created with wrong type | Write forward-fix migration to add/fix column |
| Job logs: `relation 'X' does not exist` | Migration didn't create table or dropped existing table | Write forward-fix migration to restore or create table |
| Migration succeeded in dev but failed in prod with different error | Environment-specific data issue (nullable violation, existing duplicates) | Inspect prod data, write forward-fix to handle it |
| Cannot determine failure cause from logs | Severe corruption or unrelated database issue | Escalate; consider point-in-time restore |

## Forward-Fix Migration

Create a new migration that corrects the broken schema without rolling back.

### Step 1: Identify the Failed Migration

Check migration job execution history:

```bash
migration_job_name="containerappsjob-messagebridge-migration-${REGION:0:3}-${BOOTSTRAP_SERIAL}"
db_rg="rg-messagebridge-prod-${REGION:0:3}-${BOOTSTRAP_SERIAL}"

# List recent job executions
az containerapp job execution list \
  --resource-group "$db_rg" \
  --name "$migration_job_name" \
  --query "[0:5].[name,properties.status]" -o table

# Get logs from failed execution
az containerapp job execution logs show \
  --resource-group "$db_rg" \
  --name "$migration_job_name" \
  --execution-id "<execution-id-from-above>" \
  --tail 50
```

Record the most recent failed migration ID.

### Step 2: Analyze the Broken Migration

Connect to production database (via bastion or local tunnel) and inspect the schema:

```bash
# Connect to database using OpenTofu output or Azure portal
# Database name: psql-messagebridge-prod-cin-042
# User: dbadmin (for administrative access) or app (for application-only)

psql --host=<db-name>.postgres.database.azure.com \
  --port=5432 \
  --username=dbadmin@<db-name> \
  --dbname=messagebridge \
  <<'SQL'
-- List all applied migrations
SELECT MigrationId, ProductVersion
FROM __EFMigrationsHistory
ORDER BY MigrationId DESC
LIMIT 10;

-- Inspect current schema (example)
\d YourTable
SQL
```

### Step 3: Create Forward-Fix Migration

In your development environment, create a new migration that corrects the broken state:

```bash
cd src/MessageBridge.Infrastructure

# Inspect the broken migration to understand what went wrong
cat Data/Migrations/<failed-migration>.cs

# Create a new migration that fixes the issue
dotnet ef migrations add FixPreviousMigrationName \
  --context MessageBridgeDbContext \
  --output-dir Data/Migrations
```

Edit the generated migration to include only the corrections needed. Example:

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

protected override void Down(MigrationBuilder migrationBuilder)
{
    // Reverse the corrections
    migrationBuilder.DropIndex(
        name: "IX_YourTable_MissingColumn",
        table: "YourTable");

    migrationBuilder.DropColumn(
        name: "MissingColumn",
        table: "YourTable");
}
```

### Step 4: Test Forward-Fix Locally

```bash
# Apply the forward-fix migration locally against a fresh database
dotnet ef database update \
  --context MessageBridgeDbContext

# Verify schema is correct
psql -h localhost -U app -d messagebridge -c "\d YourTable"

# Run unit tests to verify application behavior
dotnet test
```

### Step 5: Deploy Forward-Fix

Commit the migration to main:

```bash
git add src/MessageBridge.Infrastructure/Data/Migrations/<new-migration>.cs
git commit -m "fix: forward-fix migration to correct previous schema error"
git push origin main
```

Trigger deployment via delivery workflow:

```bash
# Build and test
gh workflow run delivery.yml

# Monitor workflow progress
gh run list --workflow=delivery.yml --limit=1 --json status
```

The deployment process:
1. Build and test application with new migration
2. Run manual migration job (executes both failed migration recovery and new forward-fix)
3. Deploy worker after migration succeeds

Check migration job status:

```bash
az containerapp job execution list \
  --resource-group "$db_rg" \
  --name "$migration_job_name" \
  --query "[0].properties.status" -o tsv
```

Expected output: `Succeeded`

## Point-in-Time Restore

If forward-fix is not feasible, restore the database to a point before the failed migration.

See [Database Restore Runbook](./database-restore.md) for procedures.

**Important**: Point-in-time restore requires:
- Downtime for the affected environment (dev/prod)
- Explicit confirmation that the restore is intended
- Verification that no data committed after the restore point should be preserved

## Prevention

- **Test migrations in dev first** — verify forward-fix migrations on development database before promoting
- **Code review migrations** — ensure `Up()` and `Down()` are correct before merge
- **Monitor migration jobs** — set up alerts on job failure
- **Keep backups current** — automate daily backups with 30-day retention

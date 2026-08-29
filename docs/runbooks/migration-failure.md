# Migration Failure Runbook

A delivery migration occurs before its worker revision changes. Never attempt a
down migration, run a migration job directly, or use a database credential from
the terminal.

## Diagnose (read-only)

Open the failed `delivery.yml` run and record its sanitized migration result,
delivery ref, environment, and incident ID. In Azure Portal, select the
output-derived migration job and inspect execution logs. A migration failure
means the workflow leaves the worker unchanged; stop promotion.

## Forward-fix recovery

- Target: reviewed source migration plus `delivery.yml` at its corrective ref.
- Inputs: failed migration ID, reviewed forward-fix source change, test result,
  `DELIVERY_REF`, incident ID, and selected dev/prod environment.
- Safe path: implement and test a forward-only migration in development; merge
  the approved corrective ref, then dispatch protected delivery:

  ```bash
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=dev -f environment=none
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=prod -f environment=none
  ```

- Expected result: dev migration/revision/smoke succeeds before prod becomes
  eligible; prod then uses the immutable dev handoff after protected approval.
- Failure interpretation: another migration failure stops before worker change.
  Do not retry a partial schema blindly; diagnose and create a further
  forward-fix, or use approved point-in-time recovery.
- Approval boundary: migration code review and database owner approval; prod
  additionally requires protected GitHub Environment approval.
- Cleanup: retain failed executions and migration evidence; do not delete job
  history or run a schema rollback.

## Point-in-time recovery escalation

- Target: an isolated temporary server or an explicitly authorized production
  recovery, never a worker release.
- Inputs: approved restore point, incident authorization, database-owner
  identity, and recovery change record.
- Safe path: first run the quarterly isolated procedure in
  [Database restore](./database-restore.md). For production recovery, the
  database owner uses the Azure Portal recovery path from a separately approved
  incident plan.
- Expected result: restored data/schema evidence is reviewed before any new
  forward delivery.
- Failure interpretation: uncertain data integrity means preserve evidence and
  escalate; do not substitute application rollback for database recovery.
- Approval boundary: incident commander and database owner must both approve a
  production restore.
- Cleanup: the database-restore procedure deletes only its temporary server
  after evidence is recorded and explicit confirmation is entered.

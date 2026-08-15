# Database Restore Drill

This runbook covers the quarterly PostgreSQL point-in-time restore drill: a
safe, isolated dry run that proves backups are usable and records RPO/RTO
evidence without touching the production server or its databases.

Driver script: [`scripts/db/restore-drill.sh`](../../scripts/db/restore-drill.sh).
Fixtures: [`.github/scripts/tests/restore-drill.test.sh`](../../.github/scripts/tests/restore-drill.test.sh).

## Safety model

- The driver **never** mutates the production server. It only restores a
  point-in-time snapshot to a **new, temporary** Azure Database for
  PostgreSQL Flexible Server (`psql-messagebridge-drill-<serial>-<timestamp>`).
- The temporary server is only ever destroyed after an explicit `yes`
  confirmation typed at the `destroy` prompt. There is no `--force` or
  non-interactive bypass.
- Azure AD access tokens are exported only into `PGPASSWORD` for the
  lifetime of a phase and are unset on exit. Tokens and passwords are never
  printed to stdout/stderr and never appear in command argv (`psql` reads
  the password from the environment, not a flag).
- A temporary operator-IP firewall rule is opened before each phase and
  removed on exit (success, failure, or signal) via a `trap`.

## Prerequisites

- Azure CLI (`az`) logged in as a principal with `Microsoft.DBforPostgreSQL`
  restore/read/delete permissions on the target resource group.
- `psql` available on `PATH`.
- Network egress to `api.ipify.org` (or set `MESSAGEBRIDGE_OPERATOR_IP`
  explicitly to skip that lookup).

Required environment variables:

| Variable | Example | Purpose |
|---|---|---|
| `MESSAGEBRIDGE_RESTORE_SERIAL` | `042` | Three-digit environment serial identifying the source server/resource group. |
| `MESSAGEBRIDGE_RESTORE_POINTTIME` | `2026-08-16T12:00:00Z` | ISO 8601 UTC point-in-time to restore. |
| `MESSAGEBRIDGE_OPERATOR_IP` | `203.0.113.42` | Optional. Overrides automatic public IP detection. |
| `RESTORED_SERVER` | `psql-messagebridge-drill-042-20260816T120000Z` | Optional. Targets an existing temporary server for `verify`/`destroy` instead of the freshly generated name. |

## Quarterly execution

Run each phase from the repo root. Copy/paste-safe:

```bash
export MESSAGEBRIDGE_RESTORE_SERIAL=042
export MESSAGEBRIDGE_RESTORE_POINTTIME="2026-08-16T12:00:00Z"

# 1. Plan: show what would happen, no mutations.
bash scripts/db/restore-drill.sh plan

# 2. Restore: create the temporary server and verify it in one step.
bash scripts/db/restore-drill.sh restore
```

Expected `plan` output:

```text
Source server: psql-messagebridge-shared-cin-042
Resource group: rg-messagebridge-shared-centralindia-042
Restore point: 2026-08-16T12:00:00Z
Temporary server: psql-messagebridge-drill-042-<timestamp>
Operator IP: 203.0.113.42
Plan phase: no mutations will be made.
```

Expected `restore` output ends with:

```text
Migration history verified: InitialCreate present.
Processing history verified: <N> records found.
Verification complete.
Elapsed time: 00h 0Xm 0Ys
```

`restore` prints the generated temporary server name (`Restored server:
<name>`). Record that name — it is required for a later `verify` or
`destroy` call run as a separate invocation:

```bash
export RESTORED_SERVER="psql-messagebridge-drill-042-20260816T120000Z"
bash scripts/db/restore-drill.sh verify
```

## Recording RPO/RTO evidence

Each phase prints `Elapsed time: HHh MMm SSs` measured from the start of
that invocation. For the quarterly record, capture:

- **RPO evidence**: the gap between `MESSAGEBRIDGE_RESTORE_POINTTIME` and
  the most recent record timestamp confirmed by `verify` (`message_processing_history`
  row count/recency). Compare against the 15-minute RPO objective.
- **RTO evidence**: the elapsed time printed by `restore` (firewall open
  through verification complete). Compare against the four-hour RTO
  objective.

These are operational evidence for the quarterly drill record, not a
contractual SLA — see [Integration touchpoints](../operations.md).

## Cleanup

Destruction always requires typing `yes` at the interactive prompt:

```bash
bash scripts/db/restore-drill.sh destroy
```

```text
About to destroy temporary restore server: psql-messagebridge-drill-042-<timestamp>
Enter "yes" to confirm destruction:
```

Any other input (including empty input) cancels destruction with no
mutation and exit code 0. If firewall-rule or server deletion fails, the
driver exits non-zero and reports the failure to stderr; retry `destroy`
or remove the temporary server manually via `az postgres flexible-server
delete`.

## Failure interpretation

| Symptom | Meaning | Action |
|---|---|---|
| `plan`/`restore`/`verify`/`destroy` fails with a validation error (`MESSAGEBRIDGE_RESTORE_SERIAL`/`MESSAGEBRIDGE_RESTORE_POINTTIME` message) | Input format is wrong. | Fix the environment variable and re-run; no Azure calls were made. |
| `point-in-time restore failed` | The Azure restore call itself failed (quota, permissions, invalid restore point). | Check the Azure CLI error, confirm the restore point is within the source server's retention window, retry. |
| `Server did not reach Ready state after 60 attempts` | The new server is stuck provisioning. | Check the Azure portal/CLI for the temporary server's status; if stuck, delete it manually and retry. |
| `migration history query failed` / `InitialCreate migration not found` | The restored schema does not match the expected EF Core migration baseline. | Treat as a **restore integrity failure** — see Recovery boundaries below. Do not assume the drill "passed." |
| `processing history query failed` | Could not read the processing table (connectivity, schema drift). | Re-run `verify`; if it persists, escalate — the restored data may not be trustworthy. |
| `temporary operator firewall rule cleanup failed` | Cleanup of the temporary firewall rule did not succeed. | Manually remove the rule (`FIREWALL_RULE` name printed in the error) via `az postgres flexible-server firewall-rule delete` to avoid leaving source-server firewall drift. |
| `temporary server deletion failed` | `destroy` could not delete the temporary server. | Retry `destroy`; if it persists, delete the server manually via the Azure CLI/portal to avoid ongoing cost. |

## Escalation

If `verify` fails against a genuine point-in-time restore (not a fixture
run), stop the drill, leave the temporary server running for inspection,
and escalate to the on-call database owner with:

- The restore point (`MESSAGEBRIDGE_RESTORE_POINTTIME`) and resource group.
- The full `verify` output.
- The temporary server name (`RESTORED_SERVER`), so it is not deleted
  before inspection.

Do not delete the temporary server until the escalation is resolved.

## Recovery boundaries

- A failed migration on the production server is **never** automatically
  reversed by application delivery (worker rollback never reverses schema —
  see [Architecture alignment](../deployment.md)).
- Recovery from a bad migration requires either a **forward-fix** migration
  or a genuine **point-in-time restore** performed by an operator with
  explicit authorization; this drill script only targets an isolated
  temporary server and must never be pointed at the production server name.
- This drill validates that restores are possible and that data/schema
  recency checks work; it does not itself perform any production recovery
  action.

## See Also

- [Operations Guide](../operations.md)
- [Deployment Guide](../deployment.md)

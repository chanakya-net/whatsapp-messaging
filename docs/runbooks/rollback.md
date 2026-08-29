# Worker Rollback Runbook

Rollback restores only the prior worker image through the protected delivery
workflow. It never reverses a database migration, activates a revision directly,
or guesses a resource name.

## Triage (read-only)

From GitHub Actions, identify the failed delivery run and its sanitized summary.
Use Azure Portal with the OpenTofu output-derived Container App to inspect
revision health/logs and confirm the regression is application-related. Check
CloudAMQP before treating a throughput drop as a release failure.

## Protected rollback

- Target: `delivery.yml` at the reviewed corrective ref and its dev/prod GitHub
  Environment; runtime target is resolved by the workflow from OpenTofu state.
- Inputs: `DELIVERY_REF`, failed run URL, incident/change ID, and approved
  corrective ref. No image tag, digest, resource-group name, or secret value is
  supplied manually.
- Safe path: release the corrective ref through the ordered workflow:

  ```bash
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=dev -f environment=none
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=prod -f environment=none
  ```

  If a workflow release fails after worker mutation, it captures the prior
  digest and performs its guarded rollback, health check, and smoke check.
- Expected result: dev succeeds first; prod validates that handoff, waits for
  protected production approval, and reports a healthy worker/smoke result.
- Failure interpretation: migration failure leaves the worker unchanged;
  revision or smoke failure triggers workflow rollback. A failed rollback is an
  incident: stop further dispatches and escalate with the sanitized run URL.
- Approval boundary: dev Environment policy applies; prod needs GitHub `prod`
  required reviewers and non-cancelling concurrency.
- Cleanup: retain failed and recovery run URLs, approval, and incident record;
  do not delete revision history or alter database state.

## Recovery boundaries

- Schema failure → [Migration failure](./migration-failure.md).
- Broker incident → [CloudAMQP outage](./cloudamqp-outage.md).
- Data restoration → [Database restore](./database-restore.md).
- Direct Azure revision activation/update is not an approved rollback path.

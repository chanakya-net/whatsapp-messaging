# CloudAMQP Outage Runbook

Use this runbook for broker unavailability, TLS failure, credential rotation,
or backlog. Do not directly mutate a production Container App or retrieve a
broker credential.

## Diagnose (read-only)

Use the CloudAMQP dashboard health page and Azure Portal → output-derived
Container App → Log stream. Record UTC start time, affected environment,
dashboard incident ID, and backlog trend. If CloudAMQP reports an outage, open
a vendor incident before changing any configuration.

## Recovery actions

### Vendor outage or backlog containment

- Target: CloudAMQP plan identified in the approved service record.
- Inputs: incident ID, environment, expected backlog threshold, and owner.
- Safe path: CloudAMQP dashboard → support ticket / plan controls. Pause only
  consumer controls explicitly offered by the dashboard; do not run ad-hoc
  Container Apps commands.
- Expected result: vendor acknowledges incident or dashboard recovery metrics
  show broker availability/backlog declining.
- Failure interpretation: no recovery signal means retain containment, escalate
  to vendor/on-call, and do not promote a release.
- Approval boundary: service owner approves any plan, region, or consumer-state
  change; production action requires incident commander approval.
- Cleanup: close the vendor ticket only after backlog and worker health are
  stable; attach dashboard evidence to the incident.

### Credential or endpoint replacement

- Target: output-derived environment Key Vault secret
  `rabbitmq-connection-string`.
- Inputs: approved environment, secret name, and out-of-band replacement value.
- Safe path: CloudAMQP dashboard creates/rotates the credential; Key Vault
  portal creates a new version without displaying it. Then dispatch the
  protected reload:

  ```bash
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=dev
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=prod
  ```

- Expected result: chosen workflow reload succeeds and worker health remains
  ready at its existing digest.
- Failure interpretation: stop after a failed reload; create a corrected portal
  version, preserve the prior version, and retry only after approval.
- Approval boundary: dev service owner; prod service owner plus protected GitHub
  `prod` Environment approval/concurrency.
- Cleanup: after grace period and health evidence, disable the superseded
  credential in the CloudAMQP dashboard and old Key Vault version via portal.

### Configuration mismatch

- Target: CloudAMQP dashboard vhost/TLS settings and Key Vault reference.
- Inputs: approved endpoint metadata and incident record.
- Safe path: compare dashboard metadata with the versionless reference name in
  Azure Portal; correct dashboard configuration or create a corrected portal
  secret version. Never expose the value in CLI/logs.
- Expected result: TLS connection errors cease and dashboard connections rise.
- Failure interpretation: continued failure means escalate to CloudAMQP support;
  do not alter worker revisions directly.
- Approval boundary: service owner; prod follows protected reload approval.
- Cleanup: remove accidental dashboard user/vhost only after owner approval.

## Validation and escalation

Confirm dashboard availability, Azure health/readiness telemetry, queue depth,
and absence of new connection errors for the approved observation window. A
code change or migration still uses the full publish → dev → prod delivery path
in [Deployment runbook](./deployment.md). Escalate persistent data loss or
schema symptoms to [Migration failure](./migration-failure.md) and recovery to
[Database restore](./database-restore.md).

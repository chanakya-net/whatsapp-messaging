# Secret Rotation Runbook

Rotate only approved Key Vault secrets. Enter replacement values through Azure
Portal; never retrieve, display, paste, or transport a value through CLI, logs,
workflow fields, source control, or a command argument.

Approved names: `rabbitmq-connection-string`, `new-relic-otlp-headers`,
`whatsapp-provider-placeholder`, and `email-provider-placeholder`.

## Dev rotation

- Target: vault name resolved from `.tofu/envs/dev` output and GitHub `dev`
  Environment.
- Inputs: approved secret name, out-of-band replacement value, change ID, and
  reviewed `DELIVERY_REF`.
- Safe path: Azure Portal → Key Vault → output-derived vault → Secrets → named
  secret → New Version. Confirm the portal audit record, then dispatch:

  ```bash
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=dev
  ```

- Expected result: workflow verifies versionless references and reports healthy
  reload at the current worker digest.
- Failure interpretation: retain prior version, stop, create a corrected portal
  version, and retry only after investigation.
- Approval boundary: dev secret owner approves both portal write and reload.
- Cleanup: after grace period and health evidence, disable the old version via
  portal; retain its audit history.

## Production rotation

- Target: vault name resolved from `.tofu/envs/prod` output and GitHub `prod`
  Environment.
- Inputs: same approved metadata as dev plus successful dev rotation evidence.
- Safe path: repeat the Azure Portal new-version action in prod, then dispatch:

  ```bash
  gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=prod
  ```

- Expected result: GitHub awaits required prod approval before OIDC and reports
  reload/health success with no digest change.
- Failure interpretation: do not alter a Container App directly; retain the
  prior version, investigate the portal audit and workflow summary, then create
  a corrected version if needed.
- Approval boundary: secret owner and protected prod Environment approval with
  non-cancelling concurrency.
- Cleanup: disable the prior version only after the approved grace period;
  record approval and health evidence in the change.

## Disclosure response

- Target: affected vault/version and incident record.
- Inputs: incident commander authorization and affected secret name; no value.
- Safe path: Azure Portal immediately disables the exposed version, creates a
  new version from the secure source, then uses the protected reload above.
- Expected result: audit record, healthy reload, and incident containment.
- Failure interpretation: failed reload leaves the previous healthy worker;
  escalate to the service owner and follow the related outage runbook.
- Approval boundary: incident commander and secret owner; prod Environment for
  production reload.
- Cleanup: preserve audit evidence; do not delete versions until investigation
  and retention requirements permit it.

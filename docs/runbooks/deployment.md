# Bootstrap and Deployment Runbook

Complete this runbook in order. It is for an empty account and uses only the
repository-supported `centralindia`/`cin` naming model. A missing approved
input, unexpected plan, or failed check is a stop condition: do not invent a
value, resource name, or workaround.

## Shared mutation contract

Every mutation below has the same required record:

- Target: exact OpenTofu root, GitHub Environment, Key Vault, or portal object.
- Inputs: only the stated non-secret, reviewed inputs.
- Safe path: use the shown command or portal route exactly.
- Expected result: the stated success signal appears; otherwise stop.
- Failure interpretation: no success signal means no further stage may run.
- Approval boundary: obtain the stated recorded approval before the mutation.
- Cleanup: retain the plan/run/portal audit record and remove only temporary
  artifacts named by the stage.

## Stages 1–3: authentication, inputs, and naming

### Stage 1 — Authentication

- Target: Azure subscription and `chanakya-net/whatsapp-messaging` repository.
- Inputs: operator's existing Azure and GitHub CLI sessions; no token value.
- Safe path:

  ```bash
  az account show --query '{subscription:id,tenant:tenantId}' -o json
  gh auth status
  gh repo view chanakya-net/whatsapp-messaging --json nameWithOwner,visibility
  ```

- Expected result: selected subscription/tenant metadata and repository identity
  match the approved platform record.
- Failure interpretation: an auth, scope, or repository mismatch means stop;
  request the required role from the platform owner.
- Approval boundary: no mutation; platform owner approves access before Stage 4.
- Cleanup: none.

### Stage 2 — Approved input record

Create no values in this runbook. Obtain an approved, metadata-only input
record containing: three-digit `bootstrap_serial`, Azure subscription and tenant
IDs, alert email, Entra administrator metadata, operator principal metadata,
reviewed egress `/32` ranges, allowed tenant IDs, non-sensitive tags, and the
delivery ref. It must name `centralindia`, `cin`, and repository
`chanakya-net/whatsapp-messaging`.

- Target: recorded foundation-input approval.
- Inputs: exactly the fields above; secret values are excluded.
- Safe path: store the approval record in the approved change system, not in
  shell history or a committed file.
- Expected result: platform owner signs the record before any plan.
- Failure interpretation: a blank, unreviewed, non-`/32`, or unsupported-region
  input means stop and correct the record.
- Approval boundary: platform owner approval is required.
- Cleanup: retain the record with the change ticket.

### Stage 3 — Name checks

The approved serial deterministically resolves names. Confirm only metadata:

```bash
test "$BOOTSTRAP_SERIAL" = "$(printf '%s' "$BOOTSTRAP_SERIAL" | grep -E '^[0-9]{3}$')"
printf 'location=centralindia token=cin serial=%s\n' "$BOOTSTRAP_SERIAL"
```

- Target: approved serial and fixed location model.
- Inputs: `BOOTSTRAP_SERIAL` from Stage 2.
- Safe path: run the read-only checks above.
- Expected result: a three-digit serial and `centralindia`/`cin` only.
- Failure interpretation: no match means stop; choose a newly approved serial.
- Approval boundary: no mutation.
- Cleanup: none.

## Stages 4–6: state/OIDC and foundation

### Stage 4 — Bootstrap state and OIDC

- Target: `.tofu/bootstrap`; it owns state storage, resource groups, and GitHub
  OIDC identities for shared, dev, and prod.
- Repository setup: follow the
  [GitHub Actions repository variables runbook](github-actions-variables.md)
  for the complete variable inventory, formats, validation, and CI rerun steps.
- Inputs: `MESSAGEBRIDGE_BOOTSTRAP_SERIAL` set to Stage 2's approved serial.
- Safe path:

  ```bash
  export MESSAGEBRIDGE_BOOTSTRAP_SERIAL="$BOOTSTRAP_SERIAL"
  bash scripts/infra/bootstrap.sh plan
  # Human reviews the saved bootstrap plan; reject deletes or unexpected scope.
  bash scripts/infra/bootstrap.sh apply
  # Type the script's exact displayed confirmation only after approval.
  bash scripts/infra/bootstrap.sh configure-github
  bash scripts/infra/bootstrap.sh verify
  ```

- Expected result: saved plan applied, remote bootstrap state readable, GitHub
  environments/variables configured, and `verify` succeeds.
- Failure interpretation: plan, apply, migration, or GitHub configuration error
  means stop; do not rerun `apply` until the printed cause is resolved.
- Approval boundary: platform owner signs the saved plan before `apply` and
  separately approves repository-environment configuration.
- Cleanup: retain the plan path and approval; the script preserves local state
  when backend migration fails.

### Stage 5 — Shared, dev, and prod foundation plans

For each root in exact order, create a metadata-only input file from Stage 2;
it must match that root's declared variables and contain no credential or
secret-value field. Obtain backend coordinates from bootstrap output, not a
guessed name. For each root set `ROOT`, `STATE_CONTAINER`, and `STATE_KEY` to
the matching approved bootstrap-output entries (`shared`, `dev`, then `prod`).

```bash
ROOT=.tofu/envs/shared
tofu -chdir="$ROOT" init -reconfigure -input=false \
  -backend-config="resource_group_name=$TOFU_STATE_RESOURCE_GROUP" \
  -backend-config="storage_account_name=$TOFU_STATE_STORAGE_ACCOUNT" \
  -backend-config="container_name=$STATE_CONTAINER" \
  -backend-config="key=$STATE_KEY" -backend-config="use_azuread_auth=true"
tofu -chdir="$ROOT" plan -input=false -var-file="$APPROVED_INPUT_FILE" -out="$ROOT/foundation.plan"
# Review the exact saved plan and recorded approval before this mutation.
tofu -chdir="$ROOT" apply -input=false "$ROOT/foundation.plan"
```

Repeat exactly with `ROOT=.tofu/envs/dev`, then `ROOT=.tofu/envs/prod`; use the
matching `dev` and `prod` backend entries and approved input files. Do not pass
an image digest during foundation creation; delivery supplies verified digests.

- Target: `.tofu/envs/shared`, `.tofu/envs/dev`, `.tofu/envs/prod` in order.
- Inputs: reviewed metadata-only root input file and matching bootstrap outputs.
- Safe path: saved-plan sequence above.
- Expected result: each apply creates its root's declared resources; retrieve
  names only with `tofu -chdir="$ROOT" output -json` after a successful apply.
- Failure interpretation: init/plan/apply failure means stop at that root;
  investigate state lock, input validation, or provider error before retrying.
- Approval boundary: platform owner approves each saved plan; prod needs its
  production change approval before `apply`.
- Cleanup: keep approved plans as change evidence; remove only local `*.plan`
  files after the evidence-retention period.

### Stage 6 — Placeholder secrets and database grants

For dev, then prod, obtain `vault_name` from that environment's OpenTofu output
and seed only approved placeholders. The helper has its own guarded replacement
confirmation; it never needs a real value in this runbook.

```bash
bash scripts/infra/seed-placeholder-secrets.sh --vault-name "$VAULT_NAME" --subscription "$AZURE_SUBSCRIPTION_ID"
```

Use Azure Portal → PostgreSQL server from shared OpenTofu output → Microsoft
Entra administrator to grant the output-derived runtime and migrator identities
the least privileges specified in the approved database-access change. Do not
use a password, connection string, or local database command here.

- Target: output-derived dev/prod Key Vault and shared PostgreSQL server.
- Inputs: `VAULT_NAME`, subscription ID, and approved identity/grant change.
- Safe path: helper for placeholders; Azure Portal for grants and later real
  secret replacement.
- Expected result: four placeholder names exist; portal audit shows approved
  grants; no value appears in CLI output.
- Failure interpretation: a non-placeholder replacement prompt, missing output,
  or denied grant means stop and seek platform-owner correction.
- Approval boundary: platform owner approves each vault write; database owner
  approves grants and any portal secret replacement.
- Cleanup: preserve portal audit/change IDs; remove no secret version during
  bootstrap.

## Stages 7–9: CloudAMQP, observability, GHCR

### Stage 7 — CloudAMQP

- Target: approved CloudAMQP tenant and the output-derived environment vault.
- Inputs: approved plan/region, broker endpoint metadata, and out-of-band
  credential material; never its value in a command.
- Safe path: CloudAMQP dashboard creates the broker; Key Vault portal creates a
  new version of `rabbitmq-connection-string`.
- Expected result: dashboard reports TLS broker ready and Key Vault audit shows
  a new version without displaying it.
- Failure interpretation: plan unavailable, TLS disabled, or portal error means
  stop; see [CloudAMQP outage](./cloudamqp-outage.md).
- Approval boundary: service owner approves broker cost and database owner
  approves production value replacement.
- Cleanup: delete only an accidentally created empty broker through the
  CloudAMQP dashboard after owner approval; retain the audit record.

### Stage 8 — New Relic and tenants

- Target: approved New Relic account and output-derived environment vault.
- Inputs: approved account/tenant metadata and out-of-band header material.
- Safe path: New Relic portal creates the service/alert policy; Key Vault portal
  creates a new version of `new-relic-otlp-headers`; Azure Portal records tenant
  allow-list metadata in the approved foundation change.
- Expected result: portals show the configured policy/reference, not a value.
- Failure interpretation: unavailable analytics is non-blocking; retain Azure
  platform alerts and mark New Relic deferred in the change record.
- Approval boundary: observability owner approves; prod needs change approval.
- Cleanup: remove test policy/tenant metadata only through the originating
  portal after approval.

### Stage 9 — GHCR visibility

- Target: worker and migrate packages owned by the repository.
- Inputs: authenticated GitHub session with package-admin permission.
- Safe path: GitHub web UI → repository Packages → each package → Package
  settings → Change visibility → Public; confirm repository inheritance.
- Expected result: anonymous artifact verification in `publish` can succeed.
- Failure interpretation: permission or visibility error means stop and request
  package-admin action; do not create a registry token or curl request.
- Approval boundary: repository owner approves public visibility.
- Cleanup: record package URLs and approval; do not change visibility back after
  a release without a rollback decision.

## Stages 10–12: first dev/prod release and rotations

Set `DELIVERY_REF` to Stage 2's reviewed ref. `delivery.yml` updates the
immutable migration image and runs it before changing the worker; no standalone
migration start is permitted.

```bash
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=publish -f environment=none
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=dev -f environment=none
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=prod -f environment=none
```

### Stage 10 — Publish and dev release

- Target: `delivery.yml` at `DELIVERY_REF`, then GitHub `dev` environment.
- Inputs: reviewed ref; target `publish`/`dev`; environment `none`.
- Safe path: commands above in order, then inspect the sanitized workflow run.
- Expected result: publish verifies public immutable artifacts; dev migration,
  revision, and smoke succeed and generate the dev handoff.
- Failure interpretation: publish failure changes no Azure runtime; migration
  failure changes no worker; release/smoke failure invokes workflow rollback.
- Approval boundary: dev Environment policy applies before dev mutation.
- Cleanup: retain run URL and handoff; fix/release a new ref rather than editing
  a digest or mutable tag.

### Stage 11 — Production promotion

- Target: `delivery.yml` at the same `DELIVERY_REF` and GitHub `prod` environment.
- Inputs: target `prod`, environment `none`, and the successful dev handoff.
- Safe path: third command above; approve only in the protected GitHub
  Environment when it presents the same reviewed commit.
- Expected result: workflow validates dev handoff, runs production migration,
  revises worker, smokes, and captures rollback evidence.
- Failure interpretation: failed migration leaves worker unchanged; later
  failure uses workflow rollback and stops promotion.
- Approval boundary: required prod reviewers and non-cancelling concurrency.
- Cleanup: retain run URL, approval, and sanitized result; see rollback or
  migration-failure runbook when recovery is needed.

### Stage 12 — Rotations

- Target: output-derived Key Vault and `delivery.yml` reload operation.
- Inputs: approved secret name and out-of-band replacement value; no value in
  terminal or workflow fields.
- Safe path: Key Vault portal creates new version, then run
  `reload-secrets` with explicit `DELIVERY_REF` as in
  [Secret rotation](./secret-rotation.md).
- Expected result: selected environment health check succeeds at current digest.
- Failure interpretation: reload failure retains the prior healthy revision;
  create a corrected portal version and retry after approval.
- Approval boundary: dev owner; prod owner plus protected prod Environment.
- Cleanup: retain old version for the approved grace period; disable only via
  portal after health evidence and owner approval.

## Recovery links

- [Worker rollback](./rollback.md)
- [Migration failure](./migration-failure.md)
- [Secret rotation](./secret-rotation.md)
- [CloudAMQP outage](./cloudamqp-outage.md)
- [Database restore drill](./database-restore.md)

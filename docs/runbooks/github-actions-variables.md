# GitHub Actions Repository Variables Runbook

Use this runbook to configure the repository variables consumed by the
infrastructure planning, delivery, and OIDC smoke-test workflows. Missing or
malformed values cause the `Plan shared`, `Plan dev`, and `Plan prod` jobs to
fail before Azure sign-in or `tofu plan` begins.

The repository currently uses GitHub Actions configuration variables through
the `vars` context. Add these values under **Settings → Secrets and variables
→ Actions → Variables** for `chanakya-net/whatsapp-messaging`. Do not add them
as environment variables or Actions secrets.

## Security and approval rules

- Store only reviewed, non-secret metadata in these variables.
- Never store client secrets, passwords, tokens, credentials, connection
  strings, storage keys, or private keys in any value below.
- Azure authentication uses GitHub OIDC. The workflows need managed-identity
  client IDs, never managed-identity credentials.
- Obtain the approved bootstrap serial, Azure subscription and tenant,
  principals, egress ranges, alert address, and image digests from the platform
  owner's approved input record. Do not invent example values for a real run.
- Treat bootstrap apply, GitHub repository configuration, and production input
  changes as separate approval boundaries.

## Mutation contract

- Target: repository-level GitHub Actions variables and the `shared`, `dev`,
  and `prod` GitHub environments in `chanakya-net/whatsapp-messaging`.
- Inputs: applied bootstrap outputs plus the platform owner's approved,
  metadata-only shared, development, and production input records.
- Safe path: use `scripts/infra/bootstrap.sh configure-github` for generated
  values, validate the three JSON documents locally, and upload them through
  standard input with `gh variable set`.
- Expected result: every workflow-referenced variable is present, fixed state
  coordinates match the bootstrap contract, stored JSON parses successfully,
  and the three infrastructure plan jobs proceed beyond input validation.
- Failure interpretation: any missing variable, validation error, bootstrap
  mismatch, authentication failure, or failed plan is a stop condition; do not
  guess a replacement or weaken workflow validation.
- Approval boundary: bootstrap apply, repository/environment configuration,
  and each environment's reviewed plan inputs require their recorded platform
  approval before mutation.
- Cleanup: retain the approved change record and GitHub/Azure audit history;
  remove only the temporary local JSON directory created by this runbook.

## Inventory summary

The workflows consume 20 repository variables:

- 17 values come from the applied `.tofu/bootstrap` outputs and are written by
  `scripts/infra/bootstrap.sh configure-github`.
- 3 values (`TOFU_PLAN_VARS_SHARED`, `TOFU_PLAN_VARS_DEV`, and
  `TOFU_PLAN_VARS_PROD`) contain approved, environment-specific JSON and must
  be supplied by an operator.
- The three PR plan jobs need 14 of the 20 values. The remaining 6 are used by
  delivery and OIDC boundary checks.

The bootstrap helper also records `AZURE_LOCATION`,
`TOFU_STATE_CONTAINER_BOOTSTRAP`, and `TOFU_STATE_KEY_BOOTSTRAP`. Current
workflows do not reference those three values, but they should remain as
bootstrap metadata.

## Bootstrap-generated variables

Run the bootstrap helper instead of entering these 17 values manually. This
keeps client IDs, resource groups, and backend coordinates consistent with the
applied Azure resources.

| Variable | Needed by PR plans | Why it is needed | Required source or format |
| --- | --- | --- | --- |
| `AZURE_TENANT_ID` | Yes | Selects the Entra tenant for OIDC login and the Azure provider. | Tenant UUID from the authenticated and approved Azure subscription. |
| `AZURE_SUBSCRIPTION_ID` | Yes | Selects the Azure subscription for plans and deployments. | Subscription UUID from the approved Azure account. |
| `AZURE_CLIENT_ID_PLAN` | Yes | Lets same-repository PR jobs use the read-only plan identity. | `workflow_identities.plan.client_id` from bootstrap output; use the client ID, not the object ID. |
| `AZURE_CLIENT_ID_SHARED` | No | Lets delivery jobs apply the shared root with its scoped identity. | `workflow_identities.shared.client_id` from bootstrap output. |
| `AZURE_CLIENT_ID_DEV` | No | Lets delivery and reload jobs mutate development resources with the dev-scoped identity. | `workflow_identities.dev.client_id` from bootstrap output. |
| `AZURE_CLIENT_ID_PROD` | No | Lets approved production delivery and reload jobs use the prod-scoped identity. | `workflow_identities.prod.client_id` from bootstrap output. |
| `AZURE_RESOURCE_GROUP_SHARED` | No | Gives OIDC smoke tests the allowed shared scope and a denied cross-scope target. | Shared resource-group name from bootstrap output. |
| `AZURE_RESOURCE_GROUP_DEV` | No | Gives OIDC smoke tests the allowed development scope and a denied cross-scope target. | Development resource-group name from bootstrap output. |
| `AZURE_RESOURCE_GROUP_PROD` | No | Gives OIDC smoke tests the allowed production scope and a denied cross-scope target. | Production resource-group name from bootstrap output. |
| `TOFU_STATE_RESOURCE_GROUP` | Yes | Locates the resource group containing the remote OpenTofu state account. | `rg-messagebridge-bootstrap-centralindia-NNN`, where `NNN` is the approved bootstrap serial. |
| `TOFU_STATE_STORAGE_ACCOUNT` | Yes | Locates the Azure Storage account holding remote state. | `messagebridgetfstateNNN`, using the same bootstrap serial. |
| `TOFU_STATE_CONTAINER_SHARED` | Yes | Isolates shared state from dev and prod state. | Exactly `shared`. |
| `TOFU_STATE_CONTAINER_DEV` | Yes | Isolates development state from shared and prod state. | Exactly `dev`. |
| `TOFU_STATE_CONTAINER_PROD` | Yes | Isolates production state from shared and dev state. | Exactly `prod`. |
| `TOFU_STATE_KEY_SHARED` | Yes | Selects the shared root's state object. | Exactly `messagebridge/shared.tfstate`. |
| `TOFU_STATE_KEY_DEV` | Yes | Selects the development root's state object. | Exactly `messagebridge/dev.tfstate`. |
| `TOFU_STATE_KEY_PROD` | Yes | Selects the production root's state object. | Exactly `messagebridge/prod.tfstate`. |

`NNN` must be one three-digit bootstrap serial used consistently in the state
resource-group and storage-account names.

## Operator-supplied plan variables

These variables are compact JSON objects. They contain no Azure credentials;
they describe reviewed plan inputs. Replace every angle-bracket placeholder
before validation or upload.

### `TOFU_PLAN_VARS_SHARED`

This value configures the alert recipient, PostgreSQL Entra administrator,
reviewed database egress, and optional tags for the shared root.

```json
{
  "alert_email": "<monitored-platform-email>",
  "entra_administrator": {
    "object_id": "<entra-object-uuid>",
    "principal_name": "<entra-principal-name>",
    "principal_type": "Group"
  },
  "reviewed_egress_ranges": {
    "ip-X-X-X-X": "X.X.X.X/32"
  },
  "tags": {
    "environment": "shared"
  }
}
```

Requirements:

- `alert_email` must be a valid, monitored email address.
- `object_id` must be the approved Entra principal UUID.
- `principal_name` must be non-empty.
- `principal_type` must be `Group`, `ServicePrincipal`, or `User`.
- `reviewed_egress_ranges` must be non-empty and contain unique canonical IPv4
  `/32` CIDRs.
- Each egress key must be derived from its value. For example,
  `10.20.30.40/32` must use the key `ip-10-20-30-40`.
- `0.0.0.0/32` is forbidden.
- `tags` is optional. Every tag value must be a string.

### `TOFU_PLAN_VARS_DEV`

This value configures the development alert recipient, human/operator Key
Vault access, immutable migration image, optional worker configuration, and
optional tags.

```json
{
  "alert_email": "<monitored-development-email>",
  "operator_identity": {
    "principal_id": "<development-operator-principal-uuid>",
    "principal_type": "Group"
  },
  "migration_image": {
    "repository": "ghcr.io/chanakya-net/whatsapp-messaging/migrate",
    "digest": "<64-lowercase-hex-characters>"
  },
  "worker_image": null,
  "worker_allowed_tenant_ids": null,
  "worker_otlp_endpoint": null,
  "tags": {
    "environment": "dev"
  }
}
```

### `TOFU_PLAN_VARS_PROD`

Use the same schema with independently approved production values. Do not copy
development principals, notification addresses, or unapproved image digests
into production.

```json
{
  "alert_email": "<monitored-production-email>",
  "operator_identity": {
    "principal_id": "<production-operator-principal-uuid>",
    "principal_type": "Group"
  },
  "migration_image": {
    "repository": "ghcr.io/chanakya-net/whatsapp-messaging/migrate",
    "digest": "<64-lowercase-hex-characters>"
  },
  "worker_image": null,
  "worker_allowed_tenant_ids": null,
  "worker_otlp_endpoint": null,
  "tags": {
    "environment": "prod"
  }
}
```

Development and production requirements:

- `alert_email`, `operator_identity`, and `migration_image` are required.
- `principal_id` must be an approved Entra UUID.
- `principal_type` must be `Group`, `ServicePrincipal`, or `User`.
- The migration repository must be exactly
  `ghcr.io/chanakya-net/whatsapp-messaging/migrate`.
- Image digests must be exactly 64 lowercase hexadecimal characters. Remove
  the `sha256:` prefix before storing the digest.
- `worker_image` may be omitted or `null`. When supplied, it must contain only
  `repository` and `digest`, and the digest follows the same 64-character rule.
- `worker_allowed_tenant_ids` may be omitted, `null`, or an array of non-empty
  tenant identifiers. An element must not contain a comma.
- `worker_otlp_endpoint` may be omitted, `null`, or an HTTPS URL.
- `tags` is optional. Every tag value must be a string.

## Configuration procedure

### Step 1: confirm prerequisites

Install OpenTofu 1.12.5, Azure CLI, GitHub CLI, and `jq`. Confirm that the
selected Azure account and repository match the approved platform record:

```bash
tofu version
az account show --query '{subscription:id,tenant:tenantId}' -o json
gh auth status
gh repo view chanakya-net/whatsapp-messaging --json nameWithOwner,visibility
jq --version
```

Stop if authentication, tenant, subscription, repository, or CLI versions do
not match the approved record. An expired Azure refresh token requires a fresh
`az login` before continuing.

### Step 2: apply or verify bootstrap

For a new Azure account, follow the bootstrap plan/apply sequence. Applying the
bootstrap is an Azure mutation and requires human review of the saved plan:

```bash
export MESSAGEBRIDGE_BOOTSTRAP_SERIAL="NNN"
bash scripts/infra/bootstrap.sh plan
# Review the complete saved plan and obtain approval before applying it.
bash scripts/infra/bootstrap.sh apply
```

The state account sets `shared_access_key_enabled = false`, so all state access
is Entra-only. Subscription `Owner` grants control-plane rights but no blob
data-plane rights, so `apply` and `configure-github` grant the signed-in
operator `Storage Blob Data Contributor` on the state account and wait for that
assignment to propagate. The grant is idempotent and is skipped when the
assignment already exists. The bootstrap OpenTofu roots deliberately grant blob
data roles only to the four workflow identities; the operator grant stays in
the driver script so the applied RBAC contract keeps describing CI access only.

If bootstrap has already been applied and its remote state is readable, do not
apply it again merely to repair GitHub variables. Continue to Step 3.

### Step 3: write bootstrap-generated repository variables

Repository configuration is a separate mutation and needs platform-owner
approval. The command reads applied bootstrap outputs, validates their shape,
writes repository variables with `gh variable set`, and ensures the `shared`,
`dev`, and `prod` GitHub environments exist:

```bash
export MESSAGEBRIDGE_BOOTSTRAP_SERIAL="NNN"
bash scripts/infra/bootstrap.sh configure-github
bash scripts/infra/bootstrap.sh verify
```

Do not replace this command with guessed UUIDs or names. If it reports that
remote bootstrap state does not exist, return to the approved bootstrap
plan/apply process.

### Step 4: prepare and validate the three JSON values

Create temporary files outside the repository so approved environment metadata
cannot be committed accidentally:

```bash
variable_run_dir="$(mktemp -d)"
printf 'Temporary variable files: %s\n' "$variable_run_dir"
```

Using a text editor, create these files from the templates above and replace
every placeholder:

- `$variable_run_dir/shared.json`
- `$variable_run_dir/dev.json`
- `$variable_run_dir/prod.json`

First validate JSON syntax:

```bash
jq -e 'type == "object"' "$variable_run_dir/shared.json" >/dev/null
jq -e 'type == "object"' "$variable_run_dir/dev.json" >/dev/null
jq -e 'type == "object"' "$variable_run_dir/prod.json" >/dev/null
```

Validate allowed and required top-level fields:

```bash
jq -e '
  ((keys - ["alert_email", "entra_administrator", "reviewed_egress_ranges", "tags"]) | length == 0) and
  (["alert_email", "entra_administrator", "reviewed_egress_ranges"] - keys | length == 0)
' "$variable_run_dir/shared.json" >/dev/null

for environment_name in dev prod; do
  jq -e '
    ((keys - ["alert_email", "operator_identity", "migration_image", "worker_image",
      "worker_allowed_tenant_ids", "worker_otlp_endpoint", "tags"]) | length == 0) and
    (["alert_email", "operator_identity", "migration_image"] - keys | length == 0)
  ' "$variable_run_dir/$environment_name.json" >/dev/null
done
```

The workflow performs additional UUID, email, egress, image, endpoint, and tag
validation. Review the requirements above before uploading the values.

### Step 5: add the operator-supplied variables

Use standard input so the JSON value is not written directly into shell
history:

```bash
jq -c . "$variable_run_dir/shared.json" |
  gh variable set TOFU_PLAN_VARS_SHARED --repo chanakya-net/whatsapp-messaging

jq -c . "$variable_run_dir/dev.json" |
  gh variable set TOFU_PLAN_VARS_DEV --repo chanakya-net/whatsapp-messaging

jq -c . "$variable_run_dir/prod.json" |
  gh variable set TOFU_PLAN_VARS_PROD --repo chanakya-net/whatsapp-messaging
```

Remove the temporary directory after the variables have been verified and the
approved change record has been retained:

```bash
test -n "$variable_run_dir" && test -d "$variable_run_dir"
rm -r -- "$variable_run_dir"
unset variable_run_dir
```

### Step 6: verify complete workflow-variable coverage

List configured variables without printing their values:

```bash
gh variable list --repo chanakya-net/whatsapp-messaging \
  --json name --jq '.[].name' | sort
```

Compare every variable referenced by checked-in workflows with the configured
repository-variable names. This command must produce no output:

```bash
comm -23 \
  <(rg -o --no-filename 'vars\.[A-Z0-9_]+' .github/workflows |
    cut -d. -f2 | sort -u) \
  <(gh variable list --repo chanakya-net/whatsapp-messaging \
    --json name --jq '.[].name' | sort -u)
```

Confirm the fixed backend values without printing unrelated variables:

```bash
test "$(gh variable get TOFU_STATE_CONTAINER_SHARED --repo chanakya-net/whatsapp-messaging)" = shared
test "$(gh variable get TOFU_STATE_CONTAINER_DEV --repo chanakya-net/whatsapp-messaging)" = dev
test "$(gh variable get TOFU_STATE_CONTAINER_PROD --repo chanakya-net/whatsapp-messaging)" = prod
test "$(gh variable get TOFU_STATE_KEY_SHARED --repo chanakya-net/whatsapp-messaging)" = messagebridge/shared.tfstate
test "$(gh variable get TOFU_STATE_KEY_DEV --repo chanakya-net/whatsapp-messaging)" = messagebridge/dev.tfstate
test "$(gh variable get TOFU_STATE_KEY_PROD --repo chanakya-net/whatsapp-messaging)" = messagebridge/prod.tfstate
```

Validate the three stored values without displaying them:

```bash
for environment_name in SHARED DEV PROD; do
  gh variable get "TOFU_PLAN_VARS_$environment_name" \
    --repo chanakya-net/whatsapp-messaging --json value --jq .value |
    jq -e 'type == "object"' >/dev/null
done
```

### Step 7: rerun and inspect the PR workflow

Resolve the latest infrastructure PR-plan run for the current branch, rerun
only its failed jobs, and wait for completion:

```bash
current_branch="$(git branch --show-current)"
infra_run_id="$(gh run list --repo chanakya-net/whatsapp-messaging \
  --workflow infra-plan.yml --branch "$current_branch" --event pull_request \
  --limit 1 --json databaseId --jq '.[0].databaseId')"
test -n "$infra_run_id"
gh run rerun "$infra_run_id" --repo chanakya-net/whatsapp-messaging --failed
gh run watch "$infra_run_id" --repo chanakya-net/whatsapp-messaging --exit-status
```

Successful configuration allows `Plan shared`, `Plan dev`, and `Plan prod` to
pass input validation, perform OIDC login, initialize their isolated remote
state, and create sanitized plan summaries.

## Troubleshooting

| Symptom | Likely cause | Action |
| --- | --- | --- |
| All three plan jobs fail in `Validate and prepare non-secret plan inputs`. | Core Azure/state variables are absent, or all three plan JSON values are missing. | Run `configure-github`, add all three `TOFU_PLAN_VARS_*` values, and repeat the coverage check. |
| Only one layer fails input validation. | That layer's container, key, or JSON value is missing or malformed. | Compare the layer value with its exact schema and fixed backend coordinates. |
| Azure login fails. | Client ID, tenant ID, subscription ID, or federated credential does not match the bootstrap output. | Rerun `configure-github`; do not add a client secret. |
| OpenTofu initialization fails. | State resource group, account, container, key, or plan-identity data access is incorrect. | Compare repository variables with applied bootstrap outputs and confirm the plan identity's scoped state access. |
| OpenTofu plan fails after successful initialization. | An approved input is invalid for the root, an image digest is unavailable, or Azure state differs from the expected platform record. | Inspect the sanitized failure message, correct the approved metadata or Azure state, and rerun. |
| `apply` ends with `AzureCLICredential: ERROR: Please specify only one of subscription and tenant, not both`. | A backend configuration passed both `subscription_id` and `tenant_id`, which Azure CLI 2.6x and newer reject when minting a token. | Pass only `subscription_id` in `-backend-config`. The subscription already selects the tenant. |
| `apply` ends with `AuthorizationPermissionMismatch` while migrating state. | The operator holds no blob data-plane role on the state account, and the account has no shared-key fallback. | Confirm the `Storage Blob Data Contributor` assignment on the state account, then follow the incomplete-migration recovery below. |
| `plan` stops with `incomplete backend migration; recover preserved local state before planning`. | A previous `apply` created Azure resources but failed to copy local state to the remote backend, leaving `.tofu/bootstrap/.bootstrap-migration-required`. | Recover with the procedure below. Never delete the local state file; it is the only record of the applied resources. |
| The workflow reports Node.js action deprecation warnings. | A pinned third-party action targets an older Node runtime. | Track the warning separately; it is not the empty-variable failure described by this runbook. |

Do not weaken workflow validation, add fake UUIDs, use mutable image tags, or
skip the bootstrap approval process to make a check appear green.

## Incomplete backend migration recovery

A failed migration leaves the applied Azure resources recorded only in
`.tofu/bootstrap/terraform.tfstate`, writes `backend_override.tf`, and creates
`.tofu/bootstrap/.bootstrap-migration-required`, which blocks further planning.
Rerunning `apply` is refused by design. Recover deliberately:

1. Back up `.tofu/bootstrap/terraform.tfstate` outside the repository and
   record its resource count. This file is the only record of applied
   resources until the migration completes.
2. Fix the reported cause. Missing operator blob access is the common one; see
   the troubleshooting table above.
3. Copy the preserved state into the remote backend:

   ```bash
   tofu -chdir=.tofu/bootstrap init -input=false -migrate-state -force-copy \
     -backend-config="resource_group_name=rg-messagebridge-bootstrap-centralindia-NNN" \
     -backend-config="storage_account_name=messagebridgetfstateNNN" \
     -backend-config="container_name=bootstrap" \
     -backend-config="key=messagebridge/bootstrap.tfstate" \
     -backend-config="subscription_id=<subscription-uuid>" \
     -backend-config="use_azuread_auth=true"
   ```

4. Confirm the remote state is readable and complete before clearing the
   marker. The lineage must match the backed-up file and the resource count
   must be identical:

   ```bash
   tofu -chdir=.tofu/bootstrap state pull |
     jq '{serial, lineage, resources: ([.resources[].instances | length] | add)}'
   ```

5. Only after that check passes, clear the marker and continue:

   ```bash
   rm -f .tofu/bootstrap/.bootstrap-migration-required
   bash scripts/infra/bootstrap.sh configure-github
   ```

Do not clear the marker before Step 4 confirms the remote copy. Doing so hides
an incomplete migration and risks a later apply planning to recreate resources
that already exist.

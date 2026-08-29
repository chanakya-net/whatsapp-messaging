#!/usr/bin/env bash
set -Eeuo pipefail

readonly EXPECTED_REPOSITORY="chanakya-net/whatsapp-messaging"
readonly STATE_DATA_ROLE="Storage Blob Data Contributor"
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
BOOTSTRAP_DIR="${BOOTSTRAP_DIR_OVERRIDE:-$REPO_ROOT/.tofu/bootstrap}"
BOOTSTRAP_RUNTIME_DIR="${BOOTSTRAP_RUNTIME_DIR:-$BOOTSTRAP_DIR}"
BACKEND_OVERRIDE_FILE="$BOOTSTRAP_DIR/backend_override.tf"
PLAN_FILE="$BOOTSTRAP_RUNTIME_DIR/bootstrap.plan"
MIGRATION_MARKER="$BOOTSTRAP_RUNTIME_DIR/.bootstrap-migration-required"

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

require_serial() {
  BOOTSTRAP_SERIAL="${MESSAGEBRIDGE_BOOTSTRAP_SERIAL:-}"
  [[ "$BOOTSTRAP_SERIAL" =~ ^[0-9]{3}$ ]] || fail "MESSAGEBRIDGE_BOOTSTRAP_SERIAL must be exactly three digits"
  STATE_ACCOUNT="messagebridgetfstate${BOOTSTRAP_SERIAL}"
  STATE_RESOURCE_GROUP="rg-messagebridge-bootstrap-centralindia-${BOOTSTRAP_SERIAL}"
  export BOOTSTRAP_SERIAL STATE_ACCOUNT STATE_RESOURCE_GROUP
}

load_azure_context() {
  local account_json
  account_json=$(az account show --output json) || fail "Azure CLI authentication required"
  AZURE_TENANT_ID=$(jq -er '.tenantId | select(type == "string" and length > 0)' <<<"$account_json") || fail "Azure tenant missing"
  AZURE_SUBSCRIPTION_ID=$(jq -er '.id | select(type == "string" and length > 0)' <<<"$account_json") || fail "Azure subscription missing"
  export AZURE_TENANT_ID AZURE_SUBSCRIPTION_ID
}

check_storage_name() {
  local available expected_id actual_id
  available=$(az storage account check-name --name "$STATE_ACCOUNT" --query nameAvailable -o tsv)
  if [[ "$available" == "true" ]]; then
    STATE_ACCOUNT_EXISTS=false
    export STATE_ACCOUNT_EXISTS
    return
  fi
  expected_id="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${STATE_RESOURCE_GROUP}/providers/Microsoft.Storage/storageAccounts/${STATE_ACCOUNT}"
  actual_id=$(az storage account show --name "$STATE_ACCOUNT" --subscription "$AZURE_SUBSCRIPTION_ID" --query id -o tsv 2>/dev/null || true)
  actual_id=$(printf '%s' "$actual_id" | tr '[:upper:]' '[:lower:]')
  expected_id=$(printf '%s' "$expected_id" | tr '[:upper:]' '[:lower:]')
  [[ "$actual_id" == "$expected_id" ]] || fail "state account name unavailable; choose another explicit serial"
  STATE_ACCOUNT_EXISTS=true
  export STATE_ACCOUNT_EXISTS
}

remote_backend_exists() {
  local exists
  [[ "$STATE_ACCOUNT_EXISTS" == true ]] || return 1
  exists=$(az storage container exists --auth-mode login --account-name "$STATE_ACCOUNT" --name bootstrap --query exists -o tsv 2>/dev/null) || fail "unable to inspect bootstrap state container"
  case "$exists" in
    true) return 0 ;;
    false) return 1 ;;
    *) fail "unexpected bootstrap state-container response" ;;
  esac
}

init_local_backend() {
  rm -f "$BACKEND_OVERRIDE_FILE"
  tofu -chdir="$BOOTSTRAP_DIR" init -reconfigure -input=false
}

write_remote_backend_override() {
  printf 'terraform {\n  backend "azurerm" {}\n}\n' >"$BACKEND_OVERRIDE_FILE"
}

init_remote_backend() {
  write_remote_backend_override
  tofu -chdir="$BOOTSTRAP_DIR" init -input=false -reconfigure \
    -backend-config="resource_group_name=$STATE_RESOURCE_GROUP" \
    -backend-config="storage_account_name=$STATE_ACCOUNT" \
    -backend-config="container_name=bootstrap" \
    -backend-config="key=messagebridge/bootstrap.tfstate" \
    -backend-config="subscription_id=$AZURE_SUBSCRIPTION_ID" \
    -backend-config="use_azuread_auth=true"
}

tofu_variables() {
  TOFU_VARIABLES=(
    "-var=tenant_id=$AZURE_TENANT_ID"
    "-var=subscription_id=$AZURE_SUBSCRIPTION_ID"
    "-var=bootstrap_serial=$BOOTSTRAP_SERIAL"
    "-var=repository=$EXPECTED_REPOSITORY"
  )
}

plan_bootstrap() {
  [[ ! -e "$MIGRATION_MARKER" ]] || fail "incomplete backend migration; recover preserved local state before planning"
  mkdir -p "$BOOTSTRAP_RUNTIME_DIR"
  load_azure_context
  check_storage_name
  if remote_backend_exists; then
    init_remote_backend
  else
    init_local_backend
  fi
  tofu_variables
  tofu -chdir="$BOOTSTRAP_DIR" plan -input=false -out="$PLAN_FILE" "${TOFU_VARIABLES[@]}"
  printf 'Saved plan for human review: %s\n' "$PLAN_FILE"
  tofu -chdir="$BOOTSTRAP_DIR" show "$PLAN_FILE"
}

grant_operator_state_access() {
  local operator_object_id scope existing
  operator_object_id=$(az ad signed-in-user show --query id -o tsv) ||
    fail "unable to resolve the signed-in operator object id"
  [[ -n "$operator_object_id" ]] || fail "signed-in operator object id is empty"
  scope="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${STATE_RESOURCE_GROUP}"
  scope="${scope}/providers/Microsoft.Storage/storageAccounts/${STATE_ACCOUNT}"
  existing=$(az role assignment list --assignee-object-id "$operator_object_id" \
    --scope "$scope" --role "$STATE_DATA_ROLE" --query '[0].id' -o tsv 2>/dev/null || true)
  [[ -z "$existing" ]] || return 0
  az role assignment create --assignee-object-id "$operator_object_id" \
    --assignee-principal-type User --role "$STATE_DATA_ROLE" \
    --scope "$scope" --output none ||
    fail "could not grant the operator data-plane access to the state account"
  await_state_data_plane
}

await_state_data_plane() {
  local attempt=0
  while ((attempt < 20)); do
    if az storage blob list --auth-mode login --account-name "$STATE_ACCOUNT" \
      --container-name bootstrap --output none 2>/dev/null; then
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 15
  done
  fail "operator data-plane access did not propagate to the state account"
}

migrate_local_state() {
  : >"$MIGRATION_MARKER"
  write_remote_backend_override
  if ! tofu -chdir="$BOOTSTRAP_DIR" init -input=false -migrate-state -force-copy \
    -backend-config="resource_group_name=$STATE_RESOURCE_GROUP" \
    -backend-config="storage_account_name=$STATE_ACCOUNT" \
    -backend-config="container_name=bootstrap" \
    -backend-config="key=messagebridge/bootstrap.tfstate" \
    -backend-config="subscription_id=$AZURE_SUBSCRIPTION_ID" \
    -backend-config="use_azuread_auth=true"; then
    fail "backend migration failed; local state retained and future plans blocked"
  fi
  tofu -chdir="$BOOTSTRAP_DIR" state pull >/dev/null || fail "remote bootstrap state is not readable; local state retained"
  rm -f "$MIGRATION_MARKER"
}

apply_bootstrap() {
  [[ -f "$PLAN_FILE" ]] || fail "saved plan not found; run plan first"
  [[ ! -e "$MIGRATION_MARKER" ]] || fail "incomplete backend migration requires recovery"
  load_azure_context
  check_storage_name
  local had_remote=false confirmation expected
  remote_backend_exists && had_remote=true
  printf 'Reviewing saved plan before apply: %s\n' "$PLAN_FILE"
  tofu -chdir="$BOOTSTRAP_DIR" show "$PLAN_FILE"
  expected="apply messagebridge $BOOTSTRAP_SERIAL"
  printf 'Type "%s" to apply this exact saved plan: ' "$expected"
  read -r confirmation
  [[ "$confirmation" == "$expected" ]] || fail "confirmation did not match"
  tofu -chdir="$BOOTSTRAP_DIR" apply -input=false "$PLAN_FILE"
  grant_operator_state_access
  [[ "$had_remote" == true ]] || migrate_local_state
}

validate_output_contract() {
  local output_json=$1
  jq -e '
    keys == ["custom_roles","location","resource_groups","state_backends","storage_account_id","workflow_identities","workflow_role_assignments"] and
    (.resource_groups.value | keys) == ["bootstrap","dev","prod","shared"] and
    (.state_backends.value | keys) == ["bootstrap","dev","prod","shared"] and
    (.workflow_identities.value | keys) == ["dev","plan","prod","shared"] and
    ([.location.value,
      .resource_groups.value.shared.name, .resource_groups.value.dev.name, .resource_groups.value.prod.name,
      .workflow_identities.value.plan.client_id, .workflow_identities.value.shared.client_id,
      .workflow_identities.value.dev.client_id, .workflow_identities.value.prod.client_id,
      .state_backends.value[].resource_group_name, .state_backends.value[].storage_account_name,
      .state_backends.value[].container_name, .state_backends.value[].key] | all(type == "string" and length > 0)) and
    ([.state_backends.value[].use_azuread_auth] | all(. == true))
  ' "$output_json" >/dev/null || fail "OpenTofu outputs are empty, unexpected, or unsafe"
}

github_variable_rows() {
  local output_json=$1
  printf 'AZURE_TENANT_ID\t%s\nAZURE_SUBSCRIPTION_ID\t%s\n' "$AZURE_TENANT_ID" "$AZURE_SUBSCRIPTION_ID"
  jq -r '. as $data |
    [["AZURE_LOCATION",.location.value],
     ["AZURE_CLIENT_ID_PLAN",.workflow_identities.value.plan.client_id],
     ["AZURE_CLIENT_ID_SHARED",.workflow_identities.value.shared.client_id],
     ["AZURE_CLIENT_ID_DEV",.workflow_identities.value.dev.client_id],
     ["AZURE_CLIENT_ID_PROD",.workflow_identities.value.prod.client_id],
     ["AZURE_RESOURCE_GROUP_SHARED",.resource_groups.value.shared.name],
     ["AZURE_RESOURCE_GROUP_DEV",.resource_groups.value.dev.name],
     ["AZURE_RESOURCE_GROUP_PROD",.resource_groups.value.prod.name],
     ["TOFU_STATE_RESOURCE_GROUP",.state_backends.value.bootstrap.resource_group_name],
     ["TOFU_STATE_STORAGE_ACCOUNT",.state_backends.value.bootstrap.storage_account_name]] +
    (["bootstrap","shared","dev","prod"] | map(. as $e |
      [["TOFU_STATE_CONTAINER_" + ($e|ascii_upcase),$data.state_backends.value[$e].container_name],
       ["TOFU_STATE_KEY_" + ($e|ascii_upcase),$data.state_backends.value[$e].key]]) | add) |
    .[] | @tsv
  ' "$output_json"
}

ensure_environment() {
  local environment=$1 path error_file
  path="repos/$EXPECTED_REPOSITORY/environments/$environment"
  error_file="$BOOTSTRAP_RUNTIME_DIR/gh-environment-${environment}.err"
  if gh api --method GET "$path" --silent 2>"$error_file"; then
    rm -f "$error_file"
    return
  fi
  grep -Eq '(HTTP 404|Not Found)' "$error_file" || fail "could not inspect GitHub environment: $environment"
  rm -f "$error_file"
  gh api --method PUT "$path" --silent
}

configure_github() {
  load_azure_context
  check_storage_name
  local actual_repo output_json name value
  actual_repo=$(gh repo view "$EXPECTED_REPOSITORY" --json nameWithOwner --jq .nameWithOwner)
  [[ "$actual_repo" == "$EXPECTED_REPOSITORY" ]] || fail "GitHub repository mismatch"
  remote_backend_exists || fail "remote bootstrap state must exist before GitHub configuration"
  grant_operator_state_access
  init_remote_backend
  output_json=$(mktemp "$BOOTSTRAP_RUNTIME_DIR/bootstrap-outputs.XXXXXX.json")
  tofu -chdir="$BOOTSTRAP_DIR" output -json >"$output_json"
  validate_output_contract "$output_json"
  while IFS=$'\t' read -r name value; do
    [[ -n "$name" && -n "$value" && "$value" != *$'\n'* ]] || fail "invalid repository variable output"
    gh variable set "$name" --repo "$EXPECTED_REPOSITORY" --body "$value"
  done < <(github_variable_rows "$output_json")
  rm -f "$output_json"
  for environment in shared dev prod; do
    ensure_environment "$environment"
  done
}

verify_bootstrap() {
  require_cmd tofu
  tofu -chdir="$BOOTSTRAP_DIR" fmt -check
  tofu -chdir="$BOOTSTRAP_DIR" init -backend=false -input=false
  tofu -chdir="$BOOTSTRAP_DIR" validate
  tofu -chdir="$BOOTSTRAP_DIR" test
  bash "$BOOTSTRAP_DIR/tests/bootstrap-driver.test.sh"
  bash "$BOOTSTRAP_DIR/tests/oidc-smoke-workflow.test.sh"
  if grep -REn --include='*.tf' '(client_secret|application_password|storage_account_key|connection_string)' "$BOOTSTRAP_DIR"; then
    fail "secret-bearing OpenTofu field detected"
  fi
}

usage() {
  printf 'Usage: %s {plan|apply|configure-github|verify|all}\n' "${0##*/}" >&2
  exit 2
}

main() {
  local command_name=${1:-}
  case "$command_name" in
    verify) verify_bootstrap ;;
    plan|apply|configure-github|all)
      require_cmd tofu
      require_cmd az
      require_cmd jq
      [[ "$command_name" != "configure-github" && "$command_name" != "all" ]] || require_cmd gh
      require_serial
      case "$command_name" in
        plan) plan_bootstrap ;;
        apply) apply_bootstrap ;;
        configure-github) configure_github ;;
        all) plan_bootstrap; apply_bootstrap; configure_github ;;
      esac
      ;;
    *) usage ;;
  esac
}

main "$@"

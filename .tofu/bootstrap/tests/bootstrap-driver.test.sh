#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
BOOTSTRAP_SCRIPT="$ROOT_DIR/scripts/infra/bootstrap.sh"
REQUESTED_CASE="${2:-all}"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  grep -F -- "$2" "$1" >/dev/null || fail "missing '$2' in $1"
}

assert_not_contains() {
  if grep -F -- "$2" "$1" >/dev/null; then
    fail "unexpected '$2' in $1"
  fi
}

assert_count() {
  local actual
  actual=$(grep -F -c -- "$2" "$1" || true)
  [[ "$actual" == "$3" ]] || fail "expected $3 occurrences of '$2'; found $actual"
}

make_fixture() {
  FIXTURE_DIR=$(mktemp -d)
  BIN_DIR="$FIXTURE_DIR/bin"
  BOOTSTRAP_CONFIG_DIR="$FIXTURE_DIR/bootstrap"
  CALL_LOG="$FIXTURE_DIR/calls.log"
  STUB_STATE_DIR="$FIXTURE_DIR/state"
  OUTPUT_JSON="$FIXTURE_DIR/outputs.json"
  mkdir -p "$BIN_DIR" "$BOOTSTRAP_CONFIG_DIR" "$STUB_STATE_DIR" "$FIXTURE_DIR/runtime"
  : >"$CALL_LOG"
  export FIXTURE_DIR BIN_DIR BOOTSTRAP_CONFIG_DIR CALL_LOG STUB_STATE_DIR OUTPUT_JSON
}

write_tofu_stub() {
  cat >"$BIN_DIR/tofu" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
printf 'tofu %s\n' "$*" >>"$CALL_LOG"
args=" $* "
for arg in "$@"; do
  [[ "$arg" != -chdir=* ]] || config_dir=${arg#-chdir=}
done
backend_configured() {
  grep -Eq 'backend "[^"]+"' "$config_dir"/*.tf 2>/dev/null
}
if [[ "$args" == *" init "* ]]; then
  if [[ "$args" == *" -migrate-state "* && "$args" == *" -reconfigure "* ]]; then
    exit 36
  fi
  if [[ "$args" == *" -backend=false "* ]]; then
    printf 'disabled\n' >"$STUB_STATE_DIR/backend-mode"
  elif [[ "$args" == *" -migrate-state "* ]]; then
    [[ $(cat "$STUB_STATE_DIR/backend-mode" 2>/dev/null) == local ]] || exit 37
    backend_configured || exit 38
    [[ ! -f "$STUB_STATE_DIR/fail-migration" ]] || exit 39
    printf 'remote\n' >"$STUB_STATE_DIR/backend-mode"
    touch "$STUB_STATE_DIR/remote-ready"
  elif backend_configured; then
    printf 'remote\n' >"$STUB_STATE_DIR/backend-mode"
  else
    printf 'local\n' >"$STUB_STATE_DIR/backend-mode"
  fi
elif [[ "$args" == *" plan "* ]]; then
  backend_mode=$(cat "$STUB_STATE_DIR/backend-mode" 2>/dev/null || true)
  if backend_configured; then
    [[ "$backend_mode" == remote ]] || exit 40
  else
    [[ "$backend_mode" == local ]] || exit 41
  fi
  for arg in "$@"; do
    if [[ "$arg" == -out=* ]]; then
      : >"${arg#-out=}"
    fi
  done
elif [[ "$args" == *" apply "* ]]; then
  touch "$STUB_STATE_DIR/remote-ready"
  printf '{"version":4}\n' >"$BOOTSTRAP_RUNTIME_DIR/terraform.tfstate"
elif [[ "$args" == *" state pull "* ]]; then
  printf '{"version":4}\n'
elif [[ "$args" == *" output -json "* ]]; then
  cat "$OUTPUT_JSON"
fi
STUB
  chmod +x "$BIN_DIR/tofu"
}

write_az_stub() {
  cat >"$BIN_DIR/az" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
printf 'az %s\n' "$*" >>"$CALL_LOG"
case " $* " in
  *" storage account show "*)
    if [[ -f "$STUB_STATE_DIR/name-collision" ]]; then
      printf '/subscriptions/foreign/resourceGroups/foreign/providers/Microsoft.Storage/storageAccounts/messagebridgetfstate042\n'
    else
      printf '/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-bootstrap-centralindia-042/providers/Microsoft.Storage/storageAccounts/messagebridgetfstate042\n'
    fi
    ;;
  *" account show "*)
    printf '{"tenantId":"00000000-0000-4000-8000-000000000001","id":"00000000-0000-4000-8000-000000000002"}\n'
    ;;
  *" storage account check-name "*)
    if [[ -f "$STUB_STATE_DIR/remote-ready" || -f "$STUB_STATE_DIR/name-collision" ]]; then
      printf 'false\n'
    else
      printf 'true\n'
    fi
    ;;
  *" storage container exists "*)
    [[ -f "$STUB_STATE_DIR/remote-ready" ]] && printf 'true\n' || printf 'false\n'
    ;;
  *" ad signed-in-user show "*)
    printf '00000000-0000-4000-8000-000000000003\n'
    ;;
  *" role assignment list "*)
    if [[ -f "$STUB_STATE_DIR/operator-state-access" ]]; then
      printf '/subscriptions/x/providers/Microsoft.Authorization/roleAssignments/existing\n'
    fi
    ;;
  *" role assignment create "*)
    touch "$STUB_STATE_DIR/operator-state-access"
    ;;
  *" storage blob list "*)
    if [[ ! -f "$STUB_STATE_DIR/operator-state-access" ]]; then
      printf 'AuthorizationPermissionMismatch\n' >&2
      exit 1
    fi
    ;;
  *) printf '{}\n' ;;
esac
STUB
  chmod +x "$BIN_DIR/az"
}

write_gh_stub() {
  cat >"$BIN_DIR/gh" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
printf 'gh %s\n' "$*" >>"$CALL_LOG"
if [[ " $* " == *" repo view "* ]]; then
  printf 'chanakya-net/whatsapp-messaging\n'
elif [[ " $* " == *" --method GET "*"/environments/"* ]]; then
  for arg in "$@"; do
    if [[ "$arg" == repos/*/environments/* ]]; then
      environment=${arg##*/}
    fi
  done
  if [[ "$environment" != "shared" && ! -f "$STUB_STATE_DIR/environment-$environment" ]]; then
    printf 'gh: Not Found (HTTP 404)\n' >&2
    exit 1
  fi
elif [[ " $* " == *" --method PUT "*"/environments/"* ]]; then
  for arg in "$@"; do
    if [[ "$arg" == repos/*/environments/* ]]; then
      environment=${arg##*/}
    fi
  done
  touch "$STUB_STATE_DIR/environment-$environment"
fi
STUB
  chmod +x "$BIN_DIR/gh"
}

write_output_fixture() {
  cat >"$OUTPUT_JSON" <<'JSON'
{
  "location":{"value":"centralindia"},
  "resource_groups":{"value":{"bootstrap":{"id":"b","name":"rg-messagebridge-bootstrap-centralindia-042"},"shared":{"id":"s","name":"rg-messagebridge-shared-centralindia-042"},"dev":{"id":"d","name":"rg-messagebridge-dev-centralindia-042"},"prod":{"id":"p","name":"rg-messagebridge-prod-centralindia-042"}}},
  "state_backends":{"value":{"bootstrap":{"resource_group_name":"rg-messagebridge-bootstrap-centralindia-042","storage_account_name":"messagebridgetfstate042","container_name":"bootstrap","key":"messagebridge/bootstrap.tfstate","use_azuread_auth":true},"shared":{"resource_group_name":"rg-messagebridge-bootstrap-centralindia-042","storage_account_name":"messagebridgetfstate042","container_name":"shared","key":"messagebridge/shared.tfstate","use_azuread_auth":true},"dev":{"resource_group_name":"rg-messagebridge-bootstrap-centralindia-042","storage_account_name":"messagebridgetfstate042","container_name":"dev","key":"messagebridge/dev.tfstate","use_azuread_auth":true},"prod":{"resource_group_name":"rg-messagebridge-bootstrap-centralindia-042","storage_account_name":"messagebridgetfstate042","container_name":"prod","key":"messagebridge/prod.tfstate","use_azuread_auth":true}}},
  "storage_account_id":{"value":"/subscriptions/example/storage"},
  "workflow_identities":{"value":{"plan":{"client_id":"00000000-0000-4000-8000-000000000010","principal_id":"x","resource_id":"x"},"shared":{"client_id":"00000000-0000-4000-8000-000000000011","principal_id":"x","resource_id":"x"},"dev":{"client_id":"00000000-0000-4000-8000-000000000012","principal_id":"x","resource_id":"x"},"prod":{"client_id":"00000000-0000-4000-8000-000000000013","principal_id":"x","resource_id":"x"}}},
  "custom_roles":{"value":{}},
  "workflow_role_assignments":{"value":{}}
}
JSON
}

run_bootstrap() {
  PATH="$BIN_DIR:$PATH" \
    BOOTSTRAP_DIR_OVERRIDE="$BOOTSTRAP_CONFIG_DIR" \
    BOOTSTRAP_RUNTIME_DIR="$FIXTURE_DIR/runtime" \
    MESSAGEBRIDGE_BOOTSTRAP_SERIAL=042 \
    STUB_STATE_DIR="$STUB_STATE_DIR" CALL_LOG="$CALL_LOG" OUTPUT_JSON="$OUTPUT_JSON" \
    bash "$BOOTSTRAP_SCRIPT" "$@"
}

prepare_fixture() {
  make_fixture
  write_tofu_stub
  write_az_stub
  write_gh_stub
  write_output_fixture
}

saved_plan_happy_path() {
  run_bootstrap plan
  assert_contains "$CALL_LOG" "init -reconfigure -input=false"
  assert_not_contains "$CALL_LOG" "-backend=false"
  [[ ! -e "$BOOTSTRAP_CONFIG_DIR/backend_override.tf" ]] || fail "local plan configured a remote backend"
  assert_contains "$CALL_LOG" "plan -input=false -out=$FIXTURE_DIR/runtime/bootstrap.plan"
  assert_contains "$CALL_LOG" "show $FIXTURE_DIR/runtime/bootstrap.plan"
  assert_not_contains "$CALL_LOG" " apply "

  printf 'apply messagebridge 042\n' | run_bootstrap apply
  assert_contains "$CALL_LOG" "apply -input=false $FIXTURE_DIR/runtime/bootstrap.plan"
  assert_contains "$CALL_LOG" "init -input=false -migrate-state -force-copy"
  assert_not_contains "$CALL_LOG" "-migrate-state -force-copy -reconfigure"
  assert_contains "$BOOTSTRAP_CONFIG_DIR/backend_override.tf" 'backend "azurerm" {}'
  assert_contains "$CALL_LOG" "state pull"
  assert_not_contains "$CALL_LOG" "-backend-config=tenant_id="
  assert_contains "$CALL_LOG" "role assignment create"
  assert_contains "$CALL_LOG" "Storage Blob Data Contributor"

  : >"$CALL_LOG"
  run_bootstrap plan
  assert_contains "$CALL_LOG" "init -input=false -reconfigure"
  assert_not_contains "$CALL_LOG" "-backend=false"
}

migration_failure_blocks_plans() {
  run_bootstrap plan
  touch "$STUB_STATE_DIR/fail-migration"
  if printf 'apply messagebridge 042\n' | run_bootstrap apply; then
    fail "migration failure unexpectedly succeeded"
  fi
  [[ -f "$FIXTURE_DIR/runtime/terraform.tfstate" ]] || fail "local state was not preserved"
  [[ -f "$FIXTURE_DIR/runtime/.bootstrap-migration-required" ]] || fail "migration recovery marker missing"
  : >"$CALL_LOG"
  if run_bootstrap plan; then
    fail "planning continued after failed migration"
  fi
  assert_not_contains "$CALL_LOG" " plan "
}

global_name_collision_stops() {
  touch "$STUB_STATE_DIR/name-collision"
  if run_bootstrap plan; then
    fail "foreign global-name collision did not stop planning"
  fi
  assert_not_contains "$CALL_LOG" " plan "
}

saved_plan_lifecycle() {
  prepare_fixture
  trap 'rm -rf "$FIXTURE_DIR"' RETURN
  saved_plan_happy_path

  rm -rf "$FIXTURE_DIR"
  prepare_fixture
  migration_failure_blocks_plans

  rm -rf "$FIXTURE_DIR"
  prepare_fixture
  global_name_collision_stops
}

configure_github() {
  prepare_fixture
  trap 'rm -rf "$FIXTURE_DIR"' RETURN
  touch "$STUB_STATE_DIR/remote-ready"

  run_bootstrap configure-github
  run_bootstrap configure-github

  assert_contains "$CALL_LOG" "gh repo view chanakya-net/whatsapp-messaging"
  assert_not_contains "$CALL_LOG" "gh repo view --repo"
  assert_contains "$CALL_LOG" "role assignment create"
  assert_count "$CALL_LOG" "role assignment create" 1
  assert_contains "$CALL_LOG" "gh variable set AZURE_CLIENT_ID_PROD"
  assert_contains "$CALL_LOG" "gh variable set TOFU_STATE_KEY_DEV"
  assert_not_contains "$CALL_LOG" "gh secret"
  assert_count "$CALL_LOG" "--method PUT repos/chanakya-net/whatsapp-messaging/environments/dev" 1
  assert_count "$CALL_LOG" "--method PUT repos/chanakya-net/whatsapp-messaging/environments/prod" 1
  assert_count "$CALL_LOG" "--method PUT repos/chanakya-net/whatsapp-messaging/environments/shared" 0

  jq '.unexpected = {"value":"rejected"}' "$OUTPUT_JSON" >"$OUTPUT_JSON.invalid"
  mv "$OUTPUT_JSON.invalid" "$OUTPUT_JSON"
  : >"$CALL_LOG"
  if run_bootstrap configure-github; then
    fail "unexpected output field was accepted"
  fi
  assert_not_contains "$CALL_LOG" "gh variable set"
}

case "$REQUESTED_CASE" in
  saved-plan-lifecycle) saved_plan_lifecycle ;;
  configure-github) configure_github ;;
  all)
    saved_plan_lifecycle
    configure_github
    ;;
  *) fail "unknown case: $REQUESTED_CASE" ;;
esac

printf 'PASS: bootstrap driver %s\n' "$REQUESTED_CASE"

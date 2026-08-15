#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SEEDER="$REPO_ROOT/scripts/infra/seed-placeholder-secrets.sh"
FIXTURE_SCANNER="$REPO_ROOT/.github/scripts/tests/key-vault-plan-policy.py"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_equals() {
  local expected="$1"
  local actual="$2"
  local message="$3"
  [[ "$actual" == "$expected" ]] || fail "$message (expected $expected, got $actual)"
}

assert_present() {
  local pattern="$1"
  local path="$2"
  local message="$3"
  grep -Eq "$pattern" "$path" || fail "$message"
}

assert_absent() {
  local pattern="$1"
  local message="$2"
  shift 2
  if grep -Ein "$pattern" "$@"; then
    fail "$message"
  fi
}

assert_count() {
  local expected="$1"
  local pattern="$2"
  local path="$3"
  local message="$4"
  local actual
  actual="$(grep -Ec "$pattern" "$path" || true)"
  assert_equals "$expected" "$actual" "$message"
}

run_seeder_tests() {
  setup_seeder_harness
  test_first_seed_and_idempotency
  test_inspection_failures
  test_overwrite_guard
  PATH="$SEED_TEST_DIR/empty" /bin/bash "$SEEDER" --help >/dev/null 2>&1 || fail '--help must not require Azure CLI or authentication.'
  printf '%s\n' 'Seeder contract checks passed.'
}

setup_seeder_harness() {
  SEED_TEST_DIR="$(mktemp -d)"
  FAKE_BIN="$SEED_TEST_DIR/bin"
  STATE_DIR="$SEED_TEST_DIR/state"
  CALL_LOG="$SEED_TEST_DIR/calls.log"
  STDOUT_FILE="$SEED_TEST_DIR/stdout"
  STDERR_FILE="$SEED_TEST_DIR/stderr"
  mkdir -p "$FAKE_BIN" "$STATE_DIR"
  trap 'rm -rf -- "$SEED_TEST_DIR"' EXIT

  create_fake_az "$FAKE_BIN/az"
  export AZ_FAKE_STATE_DIR="$STATE_DIR"
  export AZ_FAKE_CALL_LOG="$CALL_LOG"
}

test_first_seed_and_idempotency() {
  reset_fake_state "$STATE_DIR" "$CALL_LOG"
  invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" '' --vault-name kv-msgbr-dev-cin-042 || fail 'First seed must succeed.'
  assert_equals 4 "$(find "$STATE_DIR" -type f | wc -l | tr -d ' ')" 'First seed must create four secrets.'
  assert_equals 4 "$(count_calls '^set|' "$CALL_LOG")" 'First seed must write four secret versions.'
  assert_placeholder_state "$STATE_DIR"
  assert_output_has_no_values "$STDOUT_FILE" "$STDERR_FILE"

  : >"$CALL_LOG"
  invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" '' --vault-name kv-msgbr-dev-cin-042 || fail 'Identical rerun must succeed.'
  assert_equals 0 "$(count_calls '^set|' "$CALL_LOG")" 'Identical rerun must create no new versions.'
}

test_inspection_failures() {
  reset_fake_state "$STATE_DIR" "$CALL_LOG"
  printf '%s' 'amqps://placeholder:placeholder@rabbitmq.invalid:5671/messagebridge' >"$STATE_DIR/rabbitmq-connection-string"
  export AZ_FAKE_SHOW_FAIL_NAME='rabbitmq-connection-string'
  if invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" '' --vault-name kv-msgbr-dev-cin-042; then
    fail 'Unreadable listed secret must stop seeding.'
  fi
  unset AZ_FAKE_SHOW_FAIL_NAME
  assert_equals 0 "$(count_calls '^set|' "$CALL_LOG")" 'Inspection failure must happen before writes.'

  reset_fake_state "$STATE_DIR" "$CALL_LOG"
  export AZ_FAKE_LIST_FAIL=1
  if invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" '' --vault-name kv-msgbr-dev-cin-042; then
    fail 'Unauthorized secret listing must stop seeding.'
  fi
  unset AZ_FAKE_LIST_FAIL
  assert_equals 0 "$(count_calls '^set|' "$CALL_LOG")" 'Listing failure must happen before writes.'
}

test_overwrite_guard() {
  reset_fake_state "$STATE_DIR" "$CALL_LOG"
  printf '%s' 'amqps://do-not-print@broker.example.test/prod' >"$STATE_DIR/rabbitmq-connection-string"
  if invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" '' --vault-name kv-msgbr-prod-cin-042; then
    fail 'Non-placeholder value must be protected by default.'
  fi
  assert_equals 0 "$(count_calls '^set|' "$CALL_LOG")" 'Refusal must happen before writes.'
  assert_output_excludes 'amqps://do-not-print@broker.example.test/prod' "$STDOUT_FILE" "$STDERR_FILE"

  : >"$CALL_LOG"
  if invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" $'wrong phrase\n' --vault-name kv-msgbr-prod-cin-042 --overwrite-non-placeholder; then
    fail 'Incorrect overwrite confirmation must stop seeding.'
  fi
  assert_equals 0 "$(count_calls '^set|' "$CALL_LOG")" 'Rejected confirmation must happen before writes.'

  : >"$CALL_LOG"
  invoke_seeder "$FAKE_BIN" "$STDOUT_FILE" "$STDERR_FILE" $'overwrite kv-msgbr-prod-cin-042/rabbitmq-connection-string\n' --vault-name kv-msgbr-prod-cin-042 --overwrite-non-placeholder || fail 'Exact overwrite confirmation must allow seeding.'
  assert_equals 4 "$(count_calls '^set|' "$CALL_LOG")" 'Confirmed overwrite must update protected and missing secrets.'
  assert_placeholder_state "$STATE_DIR"
  assert_output_has_no_values "$STDOUT_FILE" "$STDERR_FILE"
}

create_fake_az() {
  local path="$1"
  apply_fake_az_fixture "$path"
  chmod +x "$path"
}

apply_fake_az_fixture() {
  local path="$1"
  printf '%s\n' '#!/usr/bin/env bash' >"$path"
  cat >>"$path" <<'FAKE_AZ'
set -euo pipefail

operation="${1:-} ${2:-} ${3:-}"
name=''
value=''
vault=''
while (($#)); do
  case "$1" in
    --name) name="$2"; shift 2 ;;
    --value) value="$2"; shift 2 ;;
    --vault-name) vault="$2"; shift 2 ;;
    *) shift ;;
  esac
done

case "$operation" in
  'keyvault secret list')
    printf 'list|%s\n' "$vault" >>"$AZ_FAKE_CALL_LOG"
    [[ "${AZ_FAKE_LIST_FAIL:-0}" != 1 ]] || exit 1
    find "$AZ_FAKE_STATE_DIR" -type f -exec basename {} \; | sort
    ;;
  'keyvault secret show')
    printf 'show|%s|%s\n' "$vault" "$name" >>"$AZ_FAKE_CALL_LOG"
    [[ "${AZ_FAKE_SHOW_FAIL_NAME:-}" != "$name" ]] || exit 1
    [[ -f "$AZ_FAKE_STATE_DIR/$name" ]] || exit 3
    printf '%s' "$(<"$AZ_FAKE_STATE_DIR/$name")"
    ;;
  'keyvault secret set')
    printf 'set|%s|%s\n' "$vault" "$name" >>"$AZ_FAKE_CALL_LOG"
    printf '%s' "$value" >"$AZ_FAKE_STATE_DIR/$name"
    ;;
  *) exit 2 ;;
esac
FAKE_AZ
}

reset_fake_state() {
  local state_dir="$1"
  local call_log="$2"
  find "$state_dir" -type f -delete
  : >"$call_log"
}

invoke_seeder() {
  local fake_bin="$1"
  local stdout_file="$2"
  local stderr_file="$3"
  local input="$4"
  shift 4
  printf '%s' "$input" | PATH="$fake_bin:$PATH" "$SEEDER" "$@" >"$stdout_file" 2>"$stderr_file"
}

count_calls() {
  local pattern="$1"
  local call_log="$2"
  grep -c "$pattern" "$call_log" || true
}

assert_placeholder_state() {
  local state_dir="$1"
  assert_equals 'amqps://placeholder:placeholder@rabbitmq.invalid:5671/messagebridge' "$(<"$state_dir/rabbitmq-connection-string")" 'RabbitMQ placeholder mismatch.'
  assert_equals 'api-key=PLACEHOLDER_NOT_A_REAL_KEY' "$(<"$state_dir/new-relic-otlp-headers")" 'New Relic placeholder mismatch.'
  assert_equals 'whatsapp-provider-disabled-placeholder' "$(<"$state_dir/whatsapp-provider-placeholder")" 'WhatsApp placeholder mismatch.'
  assert_equals 'email-provider-disabled-placeholder' "$(<"$state_dir/email-provider-placeholder")" 'Email placeholder mismatch.'
}

assert_output_has_no_values() {
  local stdout_file="$1"
  local stderr_file="$2"
  assert_output_excludes 'amqps://' "$stdout_file" "$stderr_file"
  assert_output_excludes 'api-key=' "$stdout_file" "$stderr_file"
  assert_output_excludes 'disabled-placeholder' "$stdout_file" "$stderr_file"
}

assert_output_excludes() {
  local needle="$1"
  local stdout_file="$2"
  local stderr_file="$3"
  if grep -Fq -- "$needle" "$stdout_file" "$stderr_file"; then
    fail 'Seeder output exposed a secret value.'
  fi
}

scan_fixture() {
  python3 "$FIXTURE_SCANNER" "$1"
}

run_static_policy() {
  local module_dir dev_dir prod_dir vault_block runtime_block operator_block name
  local -a tf_files
  module_dir="$REPO_ROOT/.tofu/modules/key-vault"
  dev_dir="$REPO_ROOT/.tofu/envs/dev"
  prod_dir="$REPO_ROOT/.tofu/envs/prod"
  tf_files=()
  while IFS= read -r -d '' name; do
    tf_files+=("$name")
  done < <(find "$module_dir" "$dev_dir" "$prod_dir" -maxdepth 1 -type f -name '*.tf' -print0)

  vault_block="$(sed -n '/^resource "azurerm_key_vault" "this"/,/^}/p' "$module_dir/main.tf")"
  runtime_block="$(sed -n '/^    runtime = {/,/^    }/p' "$module_dir/main.tf")"
  operator_block="$(sed -n '/^    operator = {/,/^    }/p' "$module_dir/main.tf")"

  printf '%s\n' "$vault_block" | grep -Eq 'rbac_authorization_enabled[[:space:]]*=[[:space:]]*true' || fail 'Vault RBAC authorization must be enabled.'
  printf '%s\n' "$vault_block" | grep -Eq 'purge_protection_enabled[[:space:]]*=[[:space:]]*true' || fail 'Vault purge protection must be enabled.'
  printf '%s\n' "$vault_block" | grep -Eq 'soft_delete_retention_days[[:space:]]*=[[:space:]]*90' || fail 'Vault soft-delete retention must be explicit.'
  printf '%s\n' "$vault_block" | grep -Eq 'prevent_destroy[[:space:]]*=[[:space:]]*true' || fail 'Vault destruction protection is required.'

  for name in rabbitmq-connection-string new-relic-otlp-headers whatsapp-provider-placeholder email-provider-placeholder; do
    assert_present "\"$name\"" "$module_dir/main.tf" "Approved secret name $name is missing."
  done
  assert_present 'key_vault_secret_id[[:space:]]*=[[:space:]]*"\$\{azurerm_key_vault\.this\.vault_uri\}secrets/\$\{name\}"' "$module_dir/outputs.tf" 'Container Apps references must be versionless.'

  printf '%s\n' "$runtime_block" | grep -Eq 'role_definition_name[[:space:]]*=[[:space:]]*"Key Vault Secrets User"' || fail 'Runtime must receive Key Vault Secrets User.'
  printf '%s\n' "$runtime_block" | grep -Eq 'principal_id[[:space:]]*=[[:space:]]*var\.runtime_identity\.principal_id' || fail 'Runtime reader must use runtime_identity.'
  printf '%s\n' "$operator_block" | grep -Eq 'role_definition_name[[:space:]]*=[[:space:]]*"Key Vault Secrets Officer"' || fail 'Operator must receive Key Vault Secrets Officer.'
  printf '%s\n' "$operator_block" | grep -Eq 'principal_id[[:space:]]*=[[:space:]]*var\.operator_identity\.principal_id' || fail 'Secret officer must use operator_identity.'
  assert_count 1 '"Key Vault Secrets User"' "$module_dir/main.tf" 'Exactly one reader role is allowed.'
  assert_count 1 '"Key Vault Secrets Officer"' "$module_dir/main.tf" 'Exactly one management role is allowed.'
  assert_present 'scope[[:space:]]*=[[:space:]]*azurerm_key_vault\.this\.id' "$module_dir/main.tf" 'Data-plane roles must use vault scope.'

  assert_absent '(resource|data)[[:space:]]+"azurerm_key_vault_secret"' 'OpenTofu must never manage or read secret values.' "${tf_files[@]}"
  assert_absent 'resource[[:space:]]+"azurerm_key_vault_access_policy"|access_policy[[:space:]]*\{' 'Legacy Key Vault access policies are forbidden.' "${tf_files[@]}"
  assert_absent '/secrets/[a-z0-9-]+/[a-z0-9-]+' 'Versioned secret references are forbidden.' "${tf_files[@]}"
  assert_absent 'variable[[:space:]]+"[^"]*(password|credential|token|secret[_-]?value|connection[_-]?string|api[_-]?key|otlp[_-]?headers)[^"]*"' 'Secret-bearing OpenTofu inputs are forbidden.' "${tf_files[@]}"
  assert_absent 'resource[[:space:]]+"azurerm_user_assigned_identity"' 'This slice must not duplicate runtime identities.' "${tf_files[@]}"
  assert_absent '(migrator|apply_identity|workflow_identity)' 'Migrator and apply identities must have no vault role seam.' "${tf_files[@]}"

  assert_count 1 '^module "key_vault"' "$dev_dir/key-vault.tf" 'Dev must provision exactly one vault module.'
  assert_count 1 '^module "key_vault"' "$prod_dir/key-vault.tf" 'Prod must provision exactly one vault module.'
  assert_present 'environment[[:space:]]*=[[:space:]]*"dev"' "$dev_dir/locals.tf" 'Dev environment isolation is required.'
  assert_present 'environment[[:space:]]*=[[:space:]]*"prod"' "$prod_dir/locals.tf" 'Prod environment isolation is required.'
  assert_present 'runtime_identity[[:space:]]*=[[:space:]]*var\.runtime_identity' "$dev_dir/key-vault.tf" 'Dev vault must use dependency-owned runtime identity metadata.'
  assert_present 'runtime_identity[[:space:]]*=[[:space:]]*var\.runtime_identity' "$prod_dir/key-vault.tf" 'Prod vault must use dependency-owned runtime identity metadata.'
}

run_policy_tests() {
  local fixture_dir
  fixture_dir="$REPO_ROOT/.github/scripts/tests/fixtures/key-vault-policy"

  scan_fixture "$fixture_dir/allowed-plan.json" || fail 'Safe plan fixture must pass policy scanning.'
  if scan_fixture "$fixture_dir/forbidden-secret-resource-plan.json" 2>/dev/null; then
    fail 'Managed Key Vault secret fixture must fail policy scanning.'
  fi
  if scan_fixture "$fixture_dir/forbidden-secret-data-state.json" 2>/dev/null; then
    fail 'Read Key Vault secret fixture must fail policy scanning.'
  fi
  if scan_fixture "$fixture_dir/forbidden-secret-value-state.json" 2>/dev/null; then
    fail 'Secret payload fixture must fail policy scanning.'
  fi

  run_static_policy
  printf '%s\n' 'Key Vault static and fixture policy checks passed.'
}

case "${1:-}" in
  --case)
    case "${2:-}" in
      seeder) run_seeder_tests ;;
      policy) run_policy_tests ;;
      *) fail 'Unknown test case.' ;;
    esac
    ;;
  '')
    run_seeder_tests
    run_policy_tests
    ;;
  *) fail 'Usage: key-vault-policy.test.sh [--case seeder|policy]' ;;
esac

#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
BOOTSTRAP="$REPO_ROOT/scripts/db/bootstrap-identities.sh"
BOOTSTRAP_SQL="$REPO_ROOT/scripts/db/bootstrap-identities.sql"
GRANT_MATRIX="$REPO_ROOT/.github/scripts/tests/fixtures/database-identities/grant-matrix.tsv"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_contains() {
  local needle=$1 path=$2 message=$3
  grep -Fq -- "$needle" "$path" || fail "$message"
}

assert_equals() {
  local expected=$1 actual=$2 message=$3
  [[ "$actual" == "$expected" ]] || fail "$message (expected $expected, got $actual)"
}

setup_harness() {
  TEST_DIR=$(mktemp -d)
  FAKE_BIN="$TEST_DIR/bin"
  CALL_LOG="$TEST_DIR/calls.log"
  STDOUT_FILE="$TEST_DIR/stdout"
  STDERR_FILE="$TEST_DIR/stderr"
  STATE_DIR="$TEST_DIR/state"
  mkdir -p "$FAKE_BIN" "$STATE_DIR"
  : >"$CALL_LOG"
  trap 'rm -rf -- "$TEST_DIR"' EXIT
  export DB_IDENTITIES_CALL_LOG="$CALL_LOG"
  export DB_IDENTITIES_STATE_DIR="$STATE_DIR"
  export FAKE_ACCESS_TOKEN='synthetic-token-must-never-appear'
  create_fake_commands
}

create_fake_commands() {
  cat >"$FAKE_BIN/curl" <<'FAKE_CURL'
#!/usr/bin/env bash
printf 'curl|%s\n' "$*" >>"$DB_IDENTITIES_CALL_LOG"
printf '%s\n' "${FAKE_OPERATOR_IP:-203.0.113.42}"
FAKE_CURL
  cat >"$FAKE_BIN/az" <<'FAKE_AZ'
#!/usr/bin/env bash
printf 'az|%s\n' "$*" >>"$DB_IDENTITIES_CALL_LOG"
case "${1:-} ${2:-} ${3:-}" in
  'postgres flexible-server firewall-rule')
    case "$*" in
      *' firewall-rule create '*) exit "${FAKE_FIREWALL_CREATE_EXIT:-0}" ;;
      *' firewall-rule delete '*) exit "${FAKE_FIREWALL_DELETE_EXIT:-0}" ;;
      *) exit 98 ;;
    esac
    ;;
  'account get-access-token '* )
    printf '%s\n' "$FAKE_ACCESS_TOKEN"
    ;;
  'account show '* )
    printf '%s\n' 'operator@example.test'
    ;;
  *) exit 99 ;;
esac
FAKE_AZ
  cat >"$FAKE_BIN/psql" <<'FAKE_PSQL'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" != *"$FAKE_ACCESS_TOKEN"* ]] || exit 97
count_file="$DB_IDENTITIES_STATE_DIR/psql-count"
count=0
[[ ! -f "$count_file" ]] || count=$(<"$count_file")
count=$((count + 1))
printf '%s' "$count" >"$count_file"
password_state=missing
[[ "${PGPASSWORD:-}" == "$FAKE_ACCESS_TOKEN" ]] && password_state=present
printf 'psql|call=%s|password=%s|options=%s|args=%s\n' \
  "$count" "$password_state" "${PGOPTIONS:-}" "$*" >>"$DB_IDENTITIES_CALL_LOG"
[[ "${FAKE_PSQL_FAIL_CALL:-}" != "$count" ]] || exit 42
if [[ "${FAKE_PSQL_BLOCK_CALL:-}" == "$count" ]]; then
  : >"$DB_IDENTITIES_STATE_DIR/psql-blocked"
  while [[ ! -f "$DB_IDENTITIES_STATE_DIR/release-psql" ]]; do
    sleep 0.05
  done
fi
FAKE_PSQL
  chmod +x "$FAKE_BIN/curl" "$FAKE_BIN/az" "$FAKE_BIN/psql"
}

reset_harness() {
  : >"$CALL_LOG"
  find "$STATE_DIR" -type f -delete
  unset FAKE_PSQL_FAIL_CALL FAKE_PSQL_BLOCK_CALL FAKE_FIREWALL_CREATE_EXIT
}

test_firewall_create_failure_still_cleans_up() {
  reset_harness
  export FAKE_FIREWALL_CREATE_EXIT=7
  if invoke_bootstrap apply; then
    fail 'firewall creation failure must fail apply'
  fi
  assert_equals 1 "$(grep -c 'firewall-rule delete' "$CALL_LOG")" 'create failure must attempt firewall cleanup'
  assert_token_hidden
}

invoke_bootstrap() {
  local command_name=$1
  PATH="$FAKE_BIN:$PATH" \
    MESSAGEBRIDGE_BOOTSTRAP_SERIAL=042 \
    MESSAGEBRIDGE_OPERATOR_IP=203.0.113.42 \
    "$BOOTSTRAP" "$command_name" >"$STDOUT_FILE" 2>"$STDERR_FILE"
}

assert_token_hidden() {
  if grep -Fq -- "$FAKE_ACCESS_TOKEN" "$STDOUT_FILE" "$STDERR_FILE" "$CALL_LOG"; then
    fail 'access token leaked to output or command arguments'
  fi
}

call_line() {
  local pattern=$1
  grep -n -m1 -F -- "$pattern" "$CALL_LOG" | cut -d: -f1
}

test_plan_displays_exact_targets_without_mutation() {
  reset_harness
  PATH="$FAKE_BIN:$PATH" MESSAGEBRIDGE_BOOTSTRAP_SERIAL=042 \
    "$BOOTSTRAP" plan >"$STDOUT_FILE" 2>"$STDERR_FILE" \
    || fail 'plan must succeed with target-discovery tools only'

  assert_contains 'Server: psql-messagebridge-shared-cin-042' "$STDOUT_FILE" 'plan server missing'
  assert_contains 'Resource group: rg-messagebridge-shared-centralindia-042' "$STDOUT_FILE" 'plan resource group missing'
  assert_contains 'Operator IP: 203.0.113.42' "$STDOUT_FILE" 'exact operator IP missing'
  while IFS=$'\t' read -r environment database role_type principal_template _; do
    [[ "$environment" != environment ]] || continue
    principal=${principal_template//\{serial\}/042}
    assert_contains "$database" "$STDOUT_FILE" "$environment database missing"
    assert_contains "$principal" "$STDOUT_FILE" "$role_type principal missing"
  done <"$GRANT_MATRIX"

  assert_equals 1 "$(grep -c '^curl|' "$CALL_LOG")" 'plan must resolve operator IP once'
  if grep -Eq '^(az|psql)\|' "$CALL_LOG"; then
    fail 'plan must not mutate Azure or connect to PostgreSQL'
  fi
}

test_apply_orders_work_and_cleans_up() {
  reset_harness
  invoke_bootstrap apply || fail 'apply must succeed'

  local create_line token_line delete_line last_psql_line
  create_line=$(call_line 'firewall-rule create')
  token_line=$(call_line 'account get-access-token')
  delete_line=$(call_line 'firewall-rule delete')
  last_psql_line=$(grep -n '^psql|' "$CALL_LOG" | tail -1 | cut -d: -f1)
  ((create_line < token_line && token_line < last_psql_line && last_psql_line < delete_line)) \
    || fail 'apply ordering must be firewall, token, SQL, cleanup'
  assert_contains '--start-ip-address 203.0.113.42 --end-ip-address 203.0.113.42' \
    "$CALL_LOG" 'firewall rule must use the exact operator IP'
  assert_equals 6 "$(grep -c '^psql|' "$CALL_LOG")" 'apply must create principals, grant both databases, and verify both'
  assert_equals 6 "$(grep -c 'password=present' "$CALL_LOG")" 'every psql call must receive token through PGPASSWORD'
  assert_equals 2 "$(grep -c 'messagebridge.bootstrap_mode=apply.*messagebridge.principals_only=on' "$CALL_LOG")" 'principal creation must run in postgres twice'
  assert_equals 2 "$(grep -c 'messagebridge.bootstrap_mode=apply.*messagebridge.principals_only=off' "$CALL_LOG")" 'both environment databases must be applied'
  assert_equals 2 "$(grep -c 'messagebridge.bootstrap_mode=verify' "$CALL_LOG")" 'both environment databases must be verified'
  assert_token_hidden
}

test_verify_is_read_only_and_cleans_up() {
  reset_harness
  invoke_bootstrap verify || fail 'verify must succeed'
  assert_equals 2 "$(grep -c '^psql|' "$CALL_LOG")" 'verify must inspect both databases only'
  assert_equals 2 "$(grep -c 'messagebridge.bootstrap_mode=verify' "$CALL_LOG")" 'verify mode missing'
  assert_equals 1 "$(grep -c 'firewall-rule delete' "$CALL_LOG")" 'verify must remove firewall rule'
  assert_token_hidden
}

test_failure_cleans_up() {
  reset_harness
  export FAKE_PSQL_FAIL_CALL=3
  if invoke_bootstrap apply; then
    fail 'synthetic psql failure must fail apply'
  fi
  assert_equals 1 "$(grep -c 'firewall-rule delete' "$CALL_LOG")" 'failure must remove firewall rule'
  [[ "$(tail -1 "$CALL_LOG")" == *'firewall-rule delete '* ]] \
    || fail 'firewall cleanup must be final failure-path call'
  assert_token_hidden
}

test_interrupt_cleans_up() {
  reset_harness
  export FAKE_PSQL_BLOCK_CALL=1
  python3 - "$BOOTSTRAP" "$FAKE_BIN" "$STATE_DIR" "$STDOUT_FILE" "$STDERR_FILE" <<'PY' \
    || fail 'interrupted apply must fail with cleanup'
import os
import signal
import subprocess
import sys
import time

bootstrap, fake_bin, state_dir, stdout_path, stderr_path = sys.argv[1:]
environment = os.environ.copy()
environment.update({
    "PATH": f"{fake_bin}:{environment['PATH']}",
    "MESSAGEBRIDGE_BOOTSTRAP_SERIAL": "042",
    "MESSAGEBRIDGE_OPERATOR_IP": "203.0.113.42",
})
with open(stdout_path, "w", encoding="utf-8") as stdout_file, \
     open(stderr_path, "w", encoding="utf-8") as stderr_file:
    process = subprocess.Popen(
        [bootstrap, "apply"],
        env=environment,
        stdout=stdout_file,
        stderr=stderr_file,
        start_new_session=True,
    )
    marker = os.path.join(state_dir, "psql-blocked")
    for _ in range(200):
        if os.path.exists(marker):
            break
        if process.poll() is not None:
            sys.exit(1)
        time.sleep(0.05)
    else:
        process.terminate()
        process.wait(timeout=5)
        sys.exit(1)
    os.killpg(process.pid, signal.SIGINT)
    try:
        return_code = process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)
        sys.exit(1)
sys.exit(0 if return_code != 0 else 1)
PY
  assert_equals 1 "$(grep -c 'firewall-rule delete' "$CALL_LOG")" 'interrupt must remove firewall rule'
  assert_token_hidden
}

test_static_grant_and_secret_contracts() {
  assert_contains 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES' "$BOOTSTRAP_SQL" 'runtime DML grants missing'
  assert_contains 'ALTER DEFAULT PRIVILEGES FOR ROLE' "$BOOTSTRAP_SQL" 'default privileges missing'
  assert_contains 'pg_get_userbyid(relation.relowner) <> migrator_role' "$BOOTSTRAP_SQL" 'migrator ownership verification missing'
  assert_contains 'REVOKE CONNECT ON DATABASE' "$BOOTSTRAP_SQL" 'cross-database PUBLIC denial missing'
  assert_contains "bootstrap_mode = 'apply'" "$BOOTSTRAP_SQL" 'idempotent apply branch missing'
  if grep -Fq -- '0.0.0.0' "$BOOTSTRAP"; then
    fail 'bootstrap must never use the broad Azure-services firewall address'
  fi
  if grep -Eq '(^|[[:space:]])set[[:space:]]+-x|PGPASSWORD=.*(printf|echo)|accessToken.*(printf|echo)' "$BOOTSTRAP"; then
    fail 'bootstrap must never enable tracing or print credential variables'
  fi
  if grep -Eq '^[[:space:]]*\\' "$BOOTSTRAP_SQL" \
    || sed 's/:://g' "$BOOTSTRAP_SQL" | grep -Eq ':[[:alpha:]_][[:alnum:]_]*'; then
    fail 'SQL must not contain psql meta-commands or variable substitution'
  fi
}

setup_harness
test_plan_displays_exact_targets_without_mutation
test_apply_orders_work_and_cleans_up
test_verify_is_read_only_and_cleans_up
test_firewall_create_failure_still_cleans_up
test_failure_cleans_up
test_interrupt_cleans_up
test_static_grant_and_secret_contracts
printf '%s\n' 'Database identity contract checks passed.'

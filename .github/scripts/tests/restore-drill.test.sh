#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RESTORE_DRILL="$REPO_ROOT/scripts/db/restore-drill.sh"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_contains() {
  local needle=$1 path=$2 message=$3
  grep -Fq -- "$needle" "$path" || fail "$message"
}

assert_not_contains() {
  local needle=$1 path=$2 message=$3
  grep -Fq -- "$needle" "$path" && fail "$message" || true
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
  export DB_RESTORE_CALL_LOG="$CALL_LOG"
  export DB_RESTORE_STATE_DIR="$STATE_DIR"
  export FAKE_ACCESS_TOKEN='synthetic-token-must-never-appear'
  create_fake_commands
}

create_fake_commands() {
  cat >"$FAKE_BIN/az" <<'FAKE_AZ'
#!/usr/bin/env bash
printf 'az|%s\n' "$*" >>"$DB_RESTORE_CALL_LOG"
case "${1:-} ${2:-} ${3:-}" in
  'postgres flexible-server restore')
    [[ "${FAKE_AZ_RESTORE_EXIT:-0}" == 0 ]] || exit "${FAKE_AZ_RESTORE_EXIT}"
    printf 'psql-messagebridge-drill-042-20260816T120000Z\n'
    ;;
  'postgres flexible-server show')
    printf 'Ready\n'
    ;;
  'postgres flexible-server delete')
    exit "${FAKE_AZ_DELETE_EXIT:-0}"
    ;;
  'account get-access-token '*)
    printf '%s\n' "$FAKE_ACCESS_TOKEN"
    ;;
  'account show '*)
    printf '%s\n' 'operator@example.test'
    ;;
  'postgres flexible-server firewall-rule')
    case "$*" in
      *' create '*) exit "${FAKE_FIREWALL_CREATE_EXIT:-0}" ;;
      *' delete '*) exit "${FAKE_FIREWALL_DELETE_EXIT:-0}" ;;
      *) exit 98 ;;
    esac
    ;;
  *)
    exit 99
    ;;
esac
FAKE_AZ

  cat >"$FAKE_BIN/psql" <<'FAKE_PSQL'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" != *"$FAKE_ACCESS_TOKEN"* ]] || exit 97
count_file="$DB_RESTORE_STATE_DIR/psql-count"
count=0
[[ ! -f "$count_file" ]] || count=$(<"$count_file")
count=$((count + 1))
printf '%s' "$count" >"$count_file"
printf 'psql|call=%s|args=%s\n' "$count" "$*" >>"$DB_RESTORE_CALL_LOG"

case "${FAKE_PSQL_FAIL_CALL:-}" in
  "$count") exit 42 ;;
esac

if [[ "${FAKE_PSQL_BLOCK_CALL:-}" == "$count" ]]; then
  : >"$DB_RESTORE_STATE_DIR/psql-blocked"
  while [[ ! -f "$DB_RESTORE_STATE_DIR/release-psql" ]]; do
    sleep 0.05
  done
fi

case "$*" in
  *"__EFMigrationsHistory"*"20260706100312_InitialCreate"*)
    printf '20260706100312_InitialCreate\n'
    ;;
  *"message_processing_history"*"MAX"*)
    printf '2026-08-16T11:30:00+00:00\n'
    ;;
  *"__EFMigrationsHistory"*)
    printf '(1 row)\n'
    ;;
  *"message_processing_history"*)
    printf '(1 row)\n'
    ;;
  *)
    :
    ;;
esac
FAKE_PSQL

  chmod +x "$FAKE_BIN/az" "$FAKE_BIN/psql"
}

reset_harness() {
  : >"$CALL_LOG"
  find "$STATE_DIR" -type f -delete
  unset FAKE_PSQL_FAIL_CALL FAKE_PSQL_BLOCK_CALL FAKE_FIREWALL_CREATE_EXIT FAKE_AZ_DELETE_EXIT FAKE_AZ_RESTORE_EXIT RESTORED_SERVER
}

invoke_restore_drill() {
  local command=$1
  shift || true
  PATH="$FAKE_BIN:$PATH" \
    MESSAGEBRIDGE_RESTORE_SERIAL=042 \
    MESSAGEBRIDGE_RESTORE_POINTTIME="2026-08-16T12:00:00Z" \
    MESSAGEBRIDGE_OPERATOR_IP=203.0.113.42 \
    "$RESTORE_DRILL" "$command" "$@" >"$STDOUT_FILE" 2>"$STDERR_FILE"
}

assert_token_hidden() {
  if grep -Fq -- "$FAKE_ACCESS_TOKEN" "$STDOUT_FILE" "$STDERR_FILE" "$CALL_LOG"; then
    fail 'access token leaked to output or command arguments'
  fi
}

assert_token_not_in_argv() {
  if grep "psql|.*$FAKE_ACCESS_TOKEN" "$CALL_LOG"; then
    fail 'access token must not appear in psql argv'
  fi
}

test_input_validation_serial_format() {
  reset_harness
  if MESSAGEBRIDGE_RESTORE_SERIAL=12 \
    PATH="$FAKE_BIN:$PATH" \
    "$RESTORE_DRILL" plan >"$STDOUT_FILE" 2>"$STDERR_FILE"; then
    fail 'serial validation failed to reject non-3-digit format'
  fi
  grep -Eq 'SERIAL|three digits' "$STDERR_FILE" || fail 'error message missing'
}

test_input_validation_pointtime_format() {
  reset_harness
  if MESSAGEBRIDGE_RESTORE_POINTTIME=invalid \
    PATH="$FAKE_BIN:$PATH" \
    MESSAGEBRIDGE_RESTORE_SERIAL=042 \
    "$RESTORE_DRILL" plan >"$STDOUT_FILE" 2>"$STDERR_FILE"; then
    fail 'pointtime validation failed to reject invalid format'
  fi
  grep -Eq 'POINTTIME|timestamp|ISO' "$STDERR_FILE" || fail 'error message missing'
}

test_plan_displays_targets_without_mutation() {
  reset_harness
  invoke_restore_drill plan || fail 'plan must succeed'

  assert_contains 'Source server: psql-messagebridge-shared-cin-042' "$STDOUT_FILE" 'source server missing'
  assert_contains 'Restore point: 2026-08-16T12:00:00Z' "$STDOUT_FILE" 'restore point missing'
  assert_contains 'Temporary server: psql-messagebridge-drill-042' "$STDOUT_FILE" 'temp server name missing'

  if grep -Eq '^(az|psql)\|.*restore' "$CALL_LOG"; then
    fail 'plan must not execute restore commands'
  fi
  assert_token_hidden
}

test_restore_phase_creates_restored_server() {
  reset_harness
  invoke_restore_drill restore || fail 'restore must succeed'

  assert_contains 'restore' "$CALL_LOG" 'restore command not invoked'
  assert_contains 'psql-messagebridge-drill-042' "$CALL_LOG" 'restored server not created'
  assert_token_hidden
}

test_restore_failure_reported() {
  reset_harness
  export FAKE_AZ_RESTORE_EXIT=1
  if invoke_restore_drill restore; then
    fail 'restore should fail when restore fails'
  fi
  grep -Eq 'ERROR|error|failed' "$STDERR_FILE" || fail 'error not reported'
}

test_verify_phase_checks_migration() {
  reset_harness
  invoke_restore_drill verify || fail 'verify must succeed'

  assert_contains '__EFMigrationsHistory' "$CALL_LOG" 'migration check missing'
  assert_contains 'Migration history verified' "$STDOUT_FILE" 'migration verification message missing'
  assert_token_hidden
}

test_verify_phase_checks_processing_data() {
  reset_harness
  invoke_restore_drill verify || fail 'verify must succeed'

  assert_contains 'message_processing_history' "$CALL_LOG" 'processing data check missing'
  assert_token_hidden
}

test_verify_failure_when_migration_check_fails() {
  reset_harness
  export FAKE_PSQL_FAIL_CALL=1
  if invoke_restore_drill verify; then
    fail 'verify should fail when migration check fails'
  fi
}

test_destroy_requires_confirmation() {
  reset_harness
  echo "no" | invoke_restore_drill destroy || fail_code=$?
  if grep -Eq 'psql-messagebridge-drill.*delete' "$CALL_LOG"; then
    fail 'destroy must not proceed without explicit confirmation'
  fi
}

test_destroy_with_confirmation_deletes_server() {
  reset_harness
  echo "yes" | invoke_restore_drill destroy || fail 'destroy must succeed with confirmation'

  assert_contains 'delete' "$CALL_LOG" 'delete command not invoked'
  assert_token_hidden
}

test_destroy_cleanup_fails_reported() {
  reset_harness
  export FAKE_AZ_DELETE_EXIT=7
  export RESTORED_SERVER="psql-messagebridge-drill-042-20260816T120000Z"
  if echo "yes" | invoke_restore_drill destroy; then
    fail 'destroy should fail when delete fails'
  fi
  grep -Eq 'ERROR|error|failed' "$STDERR_FILE" || fail 'error not reported'
}

test_elapsed_time_reported() {
  reset_harness
  invoke_restore_drill restore || true
  assert_contains 'Elapsed time' "$STDOUT_FILE" 'elapsed time not reported'
}

test_firewall_cleanup_on_error() {
  reset_harness
  export FAKE_PSQL_FAIL_CALL=1
  if invoke_restore_drill restore; then
    fail 'restore should fail'
  fi
  assert_contains 'firewall-rule delete' "$CALL_LOG" 'firewall cleanup not attempted'
}

test_reject_production_server_as_restore_target() {
  reset_harness
  export RESTORED_SERVER="psql-messagebridge-shared-cin-042"
  if invoke_restore_drill restore >"$STDOUT_FILE" 2>"$STDERR_FILE"; then
    fail 'restore must reject production server name'
  fi
  grep -Eq 'ERROR|reject|invalid.*name' "$STDERR_FILE" || fail 'rejection not reported'
}

test_firewall_targets_restored_server_not_source() {
  reset_harness
  invoke_restore_drill restore || fail 'restore must succeed'

  if grep 'firewall.*--name.*psql-messagebridge-shared-cin' "$CALL_LOG"; then
    fail 'firewall must not target source production server'
  fi

  grep 'firewall.*--name.*psql-messagebridge-drill' "$CALL_LOG" >/dev/null || fail 'firewall must target restored server'
}

test_verify_latest_migration_exact() {
  reset_harness
  invoke_restore_drill verify || fail 'verify must succeed'

  grep -Fq '20260706100312_InitialCreate' "$CALL_LOG" || fail 'must verify exact latest migration'
}

test_verify_processing_timestamp_query() {
  reset_harness
  invoke_restore_drill verify || fail 'verify must succeed'

  grep -E 'MAX.*created' "$CALL_LOG" || fail 'must query for MAX timestamp'
}

# Run all tests
setup_harness
test_input_validation_serial_format
test_input_validation_pointtime_format
test_plan_displays_targets_without_mutation
test_restore_phase_creates_restored_server
test_restore_failure_reported
test_verify_phase_checks_migration
test_verify_phase_checks_processing_data
test_verify_failure_when_migration_check_fails
test_destroy_requires_confirmation
test_destroy_with_confirmation_deletes_server
test_destroy_cleanup_fails_reported
test_elapsed_time_reported
test_firewall_cleanup_on_error
test_reject_production_server_as_restore_target
test_firewall_targets_restored_server_not_source
test_verify_latest_migration_exact
test_verify_processing_timestamp_query

printf '%s\n' 'All restore-drill tests passed.'

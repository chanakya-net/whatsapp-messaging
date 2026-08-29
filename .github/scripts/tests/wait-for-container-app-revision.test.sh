#!/usr/bin/env bash
# Fixture-driven tests for wait-for-container-app-revision.sh polling helper.
# Validates digest matching, health status, timeout, missing revisions, and sanitized diagnostics.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURE_DIR="$REPO_ROOT/.github/scripts/tests/fixtures/container-app-revisions"
SCRIPT_UNDER_TEST="$REPO_ROOT/.github/scripts/wait-for-container-app-revision.sh"
APP_NAME="ca-messagebridge-dev-cin-042"
REVISION_NAME="${APP_NAME}--revision-001"
RESOURCE_GROUP="rg-messagebridge-dev-centralindia-042"
POLL_BUDGET=4
DEFAULT_TIMEOUT=30

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_equals() {
  local expected=$1 actual=$2 message=$3
  [ "$actual" = "$expected" ] || fail "$message (expected '$expected', got '$actual')"
}

assert_exit_code() {
  local expected=$1 actual=$2 message=$3
  [ "$actual" -eq "$expected" ] || fail "$message (expected exit $expected, got $actual)"
}

assert_less_than() {
  local maximum=$1 actual=$2 message=$3
  [ "$actual" -lt "$maximum" ] || fail "$message (expected < $maximum, got $actual)"
}

assert_output_contains() {
  local needle=$1 output=$2 message=$3
  if ! printf '%s\n' "$output" | grep -q "$needle"; then
    fail "$message (output missing '$needle')"
  fi
}

assert_output_excludes() {
  local needle=$1 output=$2 message=$3
  if printf '%s\n' "$output" | grep -q "$needle"; then
    fail "$message (output must not contain '$needle')"
  fi
}

require_tools() {
  command -v jq >/dev/null 2>&1 || fail 'jq is required.'
  [ -x "$SCRIPT_UNDER_TEST" ] || fail "Script under test not found or not executable: $SCRIPT_UNDER_TEST"
  [ -x "$FIXTURE_DIR/fake-az.sh" ] || fail "Missing fixture: $FIXTURE_DIR/fake-az.sh"
}

setup_harness() {
  TEST_DIR="$(mktemp -d)"
  FAKE_BIN="$TEST_DIR/bin"
  mkdir -p "$FAKE_BIN"
  cp "$FIXTURE_DIR/fake-az.sh" "$FAKE_BIN/az"
  chmod +x "$FAKE_BIN/az"
  trap 'rm -rf -- "$TEST_DIR"' EXIT
  PATH="$FAKE_BIN:$PATH"
  export PATH
}

start_stream() {
  local stream=$1
  export FAKE_AZ_STREAM="$FIXTURE_DIR/$stream"
  export FAKE_AZ_STREAM_CURSOR="$TEST_DIR/${stream%.txt}.cursor"
  export FAKE_AZ_CALL_LOG="$TEST_DIR/${stream%.txt}.calls.log"
  export FAKE_AZ_APP_NAME="$APP_NAME"
  export FAKE_AZ_REVISION_NAME="$REVISION_NAME"
  : >"$FAKE_AZ_STREAM_CURSOR"
  : >"$FAKE_AZ_CALL_LOG"
  [ -f "$FAKE_AZ_STREAM" ] || fail "Missing fixture stream: $FAKE_AZ_STREAM"
}

wait_with_stream() {
  local stream=$1 expected_digest=$2
  local poll_budget=${3:-$POLL_BUDGET}
  local timeout=${4:-$DEFAULT_TIMEOUT}
  start_stream "$stream"
  "$SCRIPT_UNDER_TEST" \
    --name "$APP_NAME" \
    --revision "$REVISION_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --digest "$expected_digest" \
    --poll-budget "$poll_budget" \
    --timeout "$timeout" \
    2>&1
}

test_healthy_revision_match() {
  local output exit_code
  output="$(wait_with_stream revision-healthy-stream.txt "sha256:abc123def456")" || exit_code=$?
  exit_code=${exit_code:-0}
  assert_exit_code 0 "$exit_code" 'Healthy matching revision must exit 0'
  assert_output_contains '[Hh]ealthy' "$output" 'Output must indicate health status'
  assert_output_contains "$APP_NAME" "$output" 'Diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Diagnostics must include the revision identifier'
}

test_unhealthy_revision_fails() {
  local output exit_code
  output="$(wait_with_stream revision-unhealthy-stream.txt "sha256:abc123def456")" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Unhealthy revision must exit 1'
  assert_output_contains '[Uu]nhealthy\|[Dd]egraded' "$output" 'Output must indicate unhealthy status'
  assert_output_contains "$APP_NAME" "$output" 'Diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Diagnostics must include the revision identifier'
}

test_digest_mismatch_fails() {
  local output exit_code
  output="$(wait_with_stream revision-healthy-stream.txt "sha256:wrongdigest")" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Digest mismatch must exit 1'
  assert_output_contains 'digest' "$output" 'Output must mention digest mismatch'
}

test_provisioning_failure_fails() {
  local output exit_code
  output="$(wait_with_stream revision-provisioning-failed-stream.txt "sha256:abc123def456")" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Failed provisioning must exit 1'
  assert_output_contains 'provisioning Failed' "$output" 'Output must report terminal provisioning failure'
  assert_output_contains "$APP_NAME" "$output" 'Diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Diagnostics must include the revision identifier'
}

test_missing_revision_fails() {
  local output exit_code
  output="$(wait_with_stream revision-missing-stream.txt "sha256:abc123def456" $POLL_BUDGET)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Missing revision must exit 1'
  assert_output_contains "$APP_NAME" "$output" 'Query diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Query diagnostics must include the revision identifier'
}

test_timeout_on_nonterminal() {
  local output exit_code
  output="$(wait_with_stream revision-nonterminal-stream.txt "sha256:abc123def456" 2)" || exit_code=$?
  exit_code=${exit_code:-124}
  assert_exit_code 124 "$exit_code" 'Nonterminal must timeout with exit 124'
}

test_bounded_poll_count() {
  local calls exit_code
  start_stream revision-nonterminal-stream.txt
  "$SCRIPT_UNDER_TEST" \
    --name "$APP_NAME" \
    --revision "$REVISION_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --digest "sha256:abc123def456" \
    --poll-budget 3 \
    --timeout "$DEFAULT_TIMEOUT" \
    2>&1 >/dev/null || exit_code=$?
  calls="$(grep -c 'containerapp revision show' "$FAKE_AZ_CALL_LOG" || echo 0)"
  [ "$calls" -eq 3 ] || fail "Must stop after budget exhausted (expected 3 calls, got $calls)"
  grep -Fq -- "--revision $REVISION_NAME" "$FAKE_AZ_CALL_LOG" \
    || fail 'Revision requests must pass the required --revision identifier.'
}

test_short_timeout_does_not_overrun_boundary() {
  local output exit_code elapsed
  SECONDS=0
  output="$(wait_with_stream revision-nonterminal-stream.txt "sha256:abc123def456" 2 1)" || exit_code=$?
  elapsed=$SECONDS
  exit_code=${exit_code:-124}
  assert_exit_code 124 "$exit_code" 'Short timeout must exit 124'
  assert_less_than 2 "$elapsed" 'Short timeout must not sleep for the full poll interval'
  assert_output_contains "$APP_NAME" "$output" 'Timeout diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Timeout diagnostics must include the revision identifier'
}

test_sanitized_diagnostics_on_digest_mismatch() {
  local output exit_code
  output="$(wait_with_stream revision-healthy-stream.txt "sha256:wrongdigest" 4 5)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Mismatch must exit non-zero'
  assert_output_contains "$APP_NAME" "$output" 'Diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Diagnostics must include the revision identifier'
  # No secrets, no config values
  assert_output_excludes 'RABBITMQ\|POSTGRES\|vault\|secret' "$output" 'Diagnostics must not contain secrets'
  assert_output_excludes 'resource.group\|--resource-group' "$output" 'Diagnostics must not contain config'
}

test_sanitized_diagnostics_on_timeout() {
  local output exit_code
  output="$(wait_with_stream revision-nonterminal-stream.txt "sha256:abc123def456" 2 5)" || exit_code=$?
  exit_code=${exit_code:-124}
  assert_exit_code 124 "$exit_code" 'Timeout must exit non-zero'
  assert_output_contains "$APP_NAME" "$output" 'Diagnostics must include the app identifier'
  assert_output_contains "$REVISION_NAME" "$output" 'Diagnostics must include the revision identifier'
  # No secrets, no config values
  assert_output_excludes 'RABBITMQ\|POSTGRES\|vault\|secret' "$output" 'Diagnostics must not contain secrets'
  assert_output_excludes 'resource.group\|--resource-group' "$output" 'Diagnostics must not contain config'
}

require_tools
setup_harness
test_healthy_revision_match
test_unhealthy_revision_fails
test_digest_mismatch_fails
test_provisioning_failure_fails
test_missing_revision_fails
test_timeout_on_nonterminal
test_bounded_poll_count
test_short_timeout_does_not_overrun_boundary
test_sanitized_diagnostics_on_digest_mismatch
test_sanitized_diagnostics_on_timeout

printf '%s\n' 'All wait-for-container-app-revision tests passed.'

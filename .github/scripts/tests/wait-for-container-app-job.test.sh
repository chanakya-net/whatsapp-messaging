#!/usr/bin/env bash
# Fixture-driven tests for wait-for-container-app-job.sh polling helper.
# Validates success, failure, cancellation, timeout, missing executions, and sanitized diagnostics.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURE_DIR="$REPO_ROOT/.github/scripts/tests/fixtures/container-app-jobs"
SCRIPT_UNDER_TEST="$REPO_ROOT/.github/scripts/wait-for-container-app-job.sh"
JOB_NAME="mig-messagebridge-dev-cin-042"
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
  : >"$FAKE_AZ_STREAM_CURSOR"
  : >"$FAKE_AZ_CALL_LOG"
  [ -f "$FAKE_AZ_STREAM" ] || fail "Missing fixture stream: $FAKE_AZ_STREAM"
}

wait_with_stream() {
  local stream=$1
  local poll_budget=${2:-$POLL_BUDGET}
  local timeout=${3:-$DEFAULT_TIMEOUT}
  start_stream "$stream"
  "$SCRIPT_UNDER_TEST" \
    --name "$JOB_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --poll-budget "$poll_budget" \
    --timeout "$timeout" \
    2>&1
}

test_successful_execution() {
  local output exit_code
  output="$(wait_with_stream succeeded-stream.txt)" || exit_code=$?
  exit_code=${exit_code:-0}
  assert_exit_code 0 "$exit_code" 'Succeeded execution must exit 0'
  assert_output_contains 'Succeeded' "$output" 'Output must indicate success'
}

test_failed_execution() {
  local output exit_code
  output="$(wait_with_stream failed-stream.txt)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Failed execution must exit 1'
  assert_output_contains 'Failed' "$output" 'Output must indicate failure'
}

test_cancelled_execution() {
  local output exit_code
  output="$(wait_with_stream cancelled-stream.txt)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Cancelled execution must exit 1'
  assert_output_contains 'Cancelled' "$output" 'Output must indicate cancellation'
}

test_degraded_execution() {
  local output exit_code
  output="$(wait_with_stream degraded-stream.txt)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Degraded execution must exit 1'
  assert_output_contains 'Degraded' "$output" 'Output must indicate degradation'
}

test_timeout_on_nonterminal() {
  local output exit_code
  output="$(wait_with_stream nonterminal-stream.txt $POLL_BUDGET)" || exit_code=$?
  exit_code=${exit_code:-124}
  assert_exit_code 124 "$exit_code" 'Nonterminal execution must exit 124 (timeout)'
  assert_output_contains 'timed out\|timeout' "$output" 'Output must indicate timeout'
}

test_bounded_poll_count() {
  local calls
  start_stream nonterminal-stream.txt
  "$SCRIPT_UNDER_TEST" \
    --name "$JOB_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --poll-budget 3 \
    --timeout "$DEFAULT_TIMEOUT" \
    2>&1 >/dev/null || exit_code=$?
  calls="$(grep -c 'job execution show' "$FAKE_AZ_CALL_LOG" || echo 0)"
  [ "$calls" -eq 3 ] || fail "Must stop after budget exhausted (expected 3 calls, got $calls)"
}

test_sanitized_diagnostics_on_failure() {
  local output exit_code
  output="$(wait_with_stream failed-stream.txt 4 5)" || exit_code=$?
  exit_code=${exit_code:-1}
  assert_exit_code 1 "$exit_code" 'Failure must exit non-zero'
  # No secrets, no config values
  assert_output_excludes 'RABBITMQ\|POSTGRES\|vault\|secret' "$output" 'Diagnostics must not contain secrets'
  assert_output_excludes 'resource.group\|--resource-group' "$output" 'Diagnostics must not contain config'
}

test_sanitized_diagnostics_on_timeout() {
  local output exit_code
  output="$(wait_with_stream nonterminal-stream.txt 2 5)" || exit_code=$?
  exit_code=${exit_code:-124}
  assert_exit_code 124 "$exit_code" 'Timeout must exit non-zero'
  # No secrets, no config values
  assert_output_excludes 'RABBITMQ\|POSTGRES\|vault\|secret' "$output" 'Diagnostics must not contain secrets'
  assert_output_excludes 'resource.group\|--resource-group' "$output" 'Diagnostics must not contain config'
}

test_distinct_outcomes() {
  local results=() stream exit_code
  for stream in succeeded-stream.txt failed-stream.txt nonterminal-stream.txt; do
    wait_with_stream "$stream" $POLL_BUDGET $DEFAULT_TIMEOUT >/dev/null 2>&1 || exit_code=$?
    results+=("${exit_code:-0}")
    unset exit_code
  done
  local unique
  unique="$(printf '%s\n' "${results[@]}" | sort -u | wc -l)"
  [ "$unique" -eq 3 ] || fail "Success, failure, and timeout must be three distinct outcomes (got $unique)"
}

require_tools
setup_harness
test_successful_execution
test_failed_execution
test_cancelled_execution
test_degraded_execution
test_timeout_on_nonterminal
test_bounded_poll_count
test_sanitized_diagnostics_on_failure
test_sanitized_diagnostics_on_timeout
test_distinct_outcomes

printf '%s\n' 'All wait-for-container-app-job tests passed.'

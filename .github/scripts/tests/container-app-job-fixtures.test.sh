#!/usr/bin/env bash
# Proves that successful, failed, and timed-out Container Apps Job executions are recognised as
# distinct terminal outcomes, and that neither non-success outcome can mutate the worker revision.
#
# The observer below is a test-local reference implementation of the terminal-outcome rules. The
# production waiter is owned by a later slice; this test pins the contract it must satisfy.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURE_DIR="$REPO_ROOT/.github/scripts/tests/fixtures/container-app-jobs"
MODULE_MAIN="$REPO_ROOT/.tofu/modules/worker-workload/main.tf"
WORKER_SOURCE_DIR="$REPO_ROOT/src/MessageBridge.Worker"
JOB_NAME="mig-messagebridge-dev-cin-042"
RESOURCE_GROUP="rg-messagebridge-dev-centralindia-042"
BASELINE_REVISION="ca-messagebridge-dev-cin-042--baseline"
POLL_BUDGET=4

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_equals() {
  local expected=$1 actual=$2 message=$3
  [ "$actual" = "$expected" ] || fail "$message (expected $expected, got $actual)"
}

require_tools() {
  command -v jq >/dev/null 2>&1 || fail 'jq is required to parse Container Apps Job execution output.'
  [ -x "$FIXTURE_DIR/fake-az.sh" ] || fail "Missing executable fixture: $FIXTURE_DIR/fake-az.sh"
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
  export FAKE_AZ_WORKER_REVISION="$BASELINE_REVISION"
}

start_stream() {
  local stream=$1
  export FAKE_AZ_STREAM="$FIXTURE_DIR/$stream"
  export FAKE_AZ_STREAM_CURSOR="$TEST_DIR/${stream%.txt}.cursor"
  export FAKE_AZ_CALL_LOG="$TEST_DIR/${stream%.txt}.calls.log"
  : >"$FAKE_AZ_STREAM_CURSOR"
  : >"$FAKE_AZ_CALL_LOG"
  [ -f "$FAKE_AZ_STREAM" ] || fail "Missing execution stream fixture: $FAKE_AZ_STREAM"
}

# Reference observer: starts one execution, polls a bounded number of times, and maps the observed
# status to exactly one terminal outcome. Azure has no TimedOut status, so an execution that is
# still non-terminal when the budget is exhausted is its own outcome and never collapses to failed.
observe_execution() {
  local execution status attempt=0

  execution="$(az containerapp job start --name "$JOB_NAME" --resource-group "$RESOURCE_GROUP" --output json | jq -r '.name')"

  while [ "$attempt" -lt "$POLL_BUDGET" ]; do
    attempt=$((attempt + 1))
    status="$(az containerapp job execution show --name "$execution" --job "$JOB_NAME" \
      --resource-group "$RESOURCE_GROUP" --output json | jq -r '.properties.status')"

    case "$status" in
      Succeeded) printf 'success\n'; return 0 ;;
      Failed | Degraded | Cancelled) printf 'failed\n'; return 1 ;;
    esac
  done

  printf 'timed_out\n'
  return 124
}

observe_stream() {
  local stream=$1
  start_stream "$stream"
  OBSERVED_RESULT="$(observe_execution)" && OBSERVED_EXIT=0 || OBSERVED_EXIT=$?
}

read_worker_revision() {
  az containerapp show --name "ca-messagebridge-dev-cin-042" --resource-group "$RESOURCE_GROUP" \
    --output json | jq -r '.properties.latestRevisionName'
}

assert_no_worker_mutation() {
  local message=$1
  if grep -Eq 'containerapp (update|revision|ingress|secret)|job (update|delete)' "$FAKE_AZ_CALL_LOG"; then
    fail "$message"
  fi
}

test_successful_execution_is_terminal_success() {
  observe_stream succeeded-stream.txt
  assert_equals success "$OBSERVED_RESULT" 'A Succeeded execution must report the success outcome.'
  assert_equals 0 "$OBSERVED_EXIT" 'A Succeeded execution must exit zero.'
}

test_failed_execution_is_terminal_failure() {
  local before after
  before="$(read_worker_revision)"
  observe_stream failed-stream.txt
  after="$(read_worker_revision)"

  assert_equals failed "$OBSERVED_RESULT" 'A Failed execution must report the failed outcome.'
  assert_equals 1 "$OBSERVED_EXIT" 'A Failed execution must exit non-zero with the failed code.'
  assert_equals "$before" "$after" 'A failed migration must leave the worker revision untouched.'
  assert_equals "$BASELINE_REVISION" "$after" 'A failed migration must leave the baseline worker revision in place.'
  assert_no_worker_mutation 'A failed migration must issue no worker mutation command.'
}

test_nonterminal_execution_times_out() {
  local before after
  before="$(read_worker_revision)"
  observe_stream nonterminal-stream.txt
  after="$(read_worker_revision)"

  assert_equals timed_out "$OBSERVED_RESULT" 'A never-terminal execution must report the timed_out outcome.'
  assert_equals 124 "$OBSERVED_EXIT" 'A never-terminal execution must exit with the timeout code.'
  assert_equals "$before" "$after" 'A timed-out migration must leave the worker revision untouched.'
  assert_no_worker_mutation 'A timed-out migration must issue no worker mutation command.'
}

test_outcomes_are_distinct() {
  local results=()
  local stream
  for stream in succeeded-stream.txt failed-stream.txt nonterminal-stream.txt; do
    observe_stream "$stream"
    results+=("$OBSERVED_RESULT:$OBSERVED_EXIT")
  done

  assert_equals 3 "$(printf '%s\n' "${results[@]}" | sort -u | grep -c '')" \
    'Success, failure, and timeout must be three distinct terminal results.'
}

test_bounded_poll_budget_is_respected() {
  observe_stream nonterminal-stream.txt
  assert_equals "$POLL_BUDGET" "$(grep -c 'job execution show' "$FAKE_AZ_CALL_LOG")" \
    'The observer must stop polling once its bounded budget is exhausted.'
  assert_equals 1 "$(grep -c 'job start' "$FAKE_AZ_CALL_LOG")" \
    'The observer must start exactly one execution.'
}

migration_job_block() {
  awk '/^resource "azurerm_container_app_job" "migration"/,/^}/' "$MODULE_MAIN"
}

# Static deployment-safety contracts.

assert_no_worker_startup_migration() {
  [ -d "$WORKER_SOURCE_DIR" ] || fail "Missing worker source directory: $WORKER_SOURCE_DIR"
  if grep -REn 'Database\.Migrate|\.MigrateAsync\(|EnsureCreated\(' "$WORKER_SOURCE_DIR" >/dev/null 2>&1; then
    grep -REn 'Database\.Migrate|\.MigrateAsync\(|EnsureCreated\(' "$WORKER_SOURCE_DIR" >&2
    fail 'The worker must not run schema migrations at startup.'
  fi
}

assert_migration_job_is_worker_independent() {
  local block
  block="$(migration_job_block)"
  [ -n "$block" ] || fail 'Missing azurerm_container_app_job.migration resource.'

  local forbidden
  for forbidden in 'azurerm_container_app.worker' 'vault_references' 'key_vault' 'depends_on' \
    'local.non_secret_environment' 'local.secret_environment' 'secret {'; do
    if printf '%s\n' "$block" | grep -Fq -- "$forbidden"; then
      fail "The migration job must not reference $forbidden."
    fi
  done
}

assert_migration_job_is_manual_and_bounded() {
  local block
  block="$(migration_job_block)"

  printf '%s\n' "$block" | grep -q 'manual_trigger_config {' \
    || fail 'The migration job must declare a manual trigger.'
  printf '%s\n' "$block" | grep -Eq 'replica_timeout_in_seconds *= *1800' \
    || fail 'The migration job must bound each execution to 1800 seconds.'
  printf '%s\n' "$block" | grep -Eq 'replica_retry_limit *= *0' \
    || fail 'The migration job must never retry a failed replica.'

  local forbidden
  for forbidden in 'schedule_trigger_config' 'event_trigger_config'; do
    printf '%s\n' "$block" | grep -Fq -- "$forbidden" \
      && fail "The migration job must not declare $forbidden."
  done
  return 0
}

assert_no_automatic_execution_resource() {
  if grep -REn 'null_resource|local-exec|azapi_resource_action|containerapp job start' \
    "$REPO_ROOT/.tofu" >/dev/null 2>&1; then
    fail 'OpenTofu must never start a migration execution during apply.'
  fi
}

assert_worker_revision_is_not_job_owned() {
  grep -q 'value       = azurerm_container_app.worker.latest_revision_name' \
    "$REPO_ROOT/.tofu/modules/worker-workload/outputs.tf" \
    || fail 'The worker revision output must be derived only from the worker container app.'

  local env_root
  for env_root in dev prod; do
    if grep -Fq 'depends_on' "$REPO_ROOT/.tofu/envs/$env_root/worker.tf"; then
      fail "The $env_root workload module must not carry a module-wide depends_on that couples the job."
    fi
  done
}

# Negative probes: the contract evaluators must reject the violations they exist to catch.

assert_static_evaluators_reject_violations() {
  local probe_dir="$TEST_DIR/probe"
  mkdir -p "$probe_dir"
  printf 'context.Database.Migrate();\n' >"$probe_dir/Program.cs"

  # Each probe runs in a subshell because a rejecting contract exits the shell it runs in.
  if (WORKER_SOURCE_DIR="$probe_dir" assert_no_worker_startup_migration) 2>/dev/null; then
    fail 'The startup-migration contract accepted a Database.Migrate call.'
  fi

  printf 'az|containerapp update --name ca-messagebridge-dev-cin-042\n' >"$TEST_DIR/probe.calls.log"
  if (FAKE_AZ_CALL_LOG="$TEST_DIR/probe.calls.log" assert_no_worker_mutation 'probe') 2>/dev/null; then
    fail 'The worker mutation contract accepted a containerapp update call.'
  fi

  printf 'resource "azurerm_container_app_job" "migration" {\n  depends_on = [azurerm_container_app.worker]\n}\n' \
    >"$TEST_DIR/probe-main.tf"
  if (MODULE_MAIN="$TEST_DIR/probe-main.tf" assert_migration_job_is_worker_independent) 2>/dev/null; then
    fail 'The job independence contract accepted a worker dependency.'
  fi

  printf 'resource "azurerm_container_app_job" "migration" {\n  schedule_trigger_config {}\n}\n' \
    >"$TEST_DIR/probe-schedule.tf"
  if (MODULE_MAIN="$TEST_DIR/probe-schedule.tf" assert_migration_job_is_manual_and_bounded) 2>/dev/null; then
    fail 'The manual-trigger contract accepted a scheduled trigger.'
  fi
}

require_tools
setup_harness
test_successful_execution_is_terminal_success
test_failed_execution_is_terminal_failure
test_nonterminal_execution_times_out
test_outcomes_are_distinct
test_bounded_poll_budget_is_respected
assert_static_evaluators_reject_violations
assert_no_worker_startup_migration
assert_migration_job_is_worker_independent
assert_migration_job_is_manual_and_bounded
assert_no_automatic_execution_resource
assert_worker_revision_is_not_job_owned

printf '%s\n' 'Container Apps Job execution and deployment-safety contracts passed.'

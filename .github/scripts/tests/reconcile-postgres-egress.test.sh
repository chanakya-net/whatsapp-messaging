#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/postgres-egress"
SCRIPT="$REPO_ROOT/.github/scripts/reconcile-postgres-egress.sh"
DATABASE_MAIN="$REPO_ROOT/.tofu/modules/database/main.tf"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

setup() {
  command -v jq >/dev/null || fail 'jq is required'
  TEST_DIR="$(mktemp -d)"
  mkdir -p "$TEST_DIR/bin" "$TEST_DIR/states/dev" "$TEST_DIR/states/prod"
  cp "$FIXTURES/fake-tofu.sh" "$TEST_DIR/bin/tofu"
  cp "$FIXTURES/fake-az.sh" "$TEST_DIR/bin/az"
  chmod +x "$TEST_DIR/bin/tofu" "$TEST_DIR/bin/az"
  export PATH="$TEST_DIR/bin:$PATH"
  export FAKE_CASES_FILE="$FIXTURES/cases.json"
  export FAKE_CALL_LOG="$TEST_DIR/calls.log"
  trap 'rm -rf -- "$TEST_DIR"' EXIT
}

run_reconcile() {
  local scenario=$1 mode=$2 output=$3
  shift 3
  export FAKE_SCENARIO="$scenario"
  : >"$FAKE_CALL_LOG"
  "$SCRIPT" "$mode" \
    --dev-root "$TEST_DIR/states/dev" \
    --prod-root "$TEST_DIR/states/prod" \
    --resource-group rg-shared \
    --server-name psql-shared \
    --output "$output" "$@"
}

assert_fails() {
  local scenario=$1 mode
  shift
  mode=${1:-exact}
  [ "$#" -eq 0 ] || shift
  if run_reconcile "$scenario" "$mode" "$TEST_DIR/$scenario.tfvars.json" "$@" >/dev/null 2>&1; then
    fail "$scenario $mode must fail closed"
  fi
}

assert_has_range() {
  local file=$1 range=$2
  jq -e --arg range "$range" '.reviewed_egress_ranges | any(. == $range)' "$file" >/dev/null ||
    fail "$file missing $range"
}

assert_lacks_range() {
  local file=$1 range=$2
  if jq -e --arg range "$range" '.reviewed_egress_ranges | any(. == $range)' "$file" >/dev/null; then
    fail "$file unexpectedly contains $range"
  fi
}

test_collection_normalization_and_guards() {
  local output="$TEST_DIR/exact.tfvars.json"
  run_reconcile unchanged exact "$output" >/dev/null 2>&1
  [ "$(jq '.reviewed_egress_ranges | length' "$output")" -eq 8 ] || fail 'unchanged union must contain eight ranges'
  jq -e '.reviewed_egress_ranges["ip-10-0-0-2"] == "10.0.0.2/32"' "$output" >/dev/null || fail 'stable CIDR key missing'

  run_reconcile duplicates exact "$output" >/dev/null 2>&1
  [ "$(jq '.reviewed_egress_ranges | length' "$output")" -eq 8 ] || fail 'duplicates must normalize to one range'

  local scenario
  for scenario in empty incomplete missing_environment invalid_cidr broad declared_mismatch; do
    assert_fails "$scenario"
  done
}

test_retained_exact_and_verify_transitions() {
  local retained="$TEST_DIR/retained.tfvars.json" exact="$TEST_DIR/transition-exact.tfvars.json"
  run_reconcile additions retained "$retained" >/dev/null 2>&1
  assert_has_range "$retained" "10.0.0.5/32"

  run_reconcile removals retained "$retained" >/dev/null 2>&1
  assert_has_range "$retained" "10.0.0.6/32"

  run_reconcile retained_transition retained "$retained" >/dev/null 2>&1
  assert_has_range "$retained" "10.0.0.4/32"
  assert_has_range "$retained" "10.0.0.5/32"
  run_reconcile retained_transition exact "$exact" >/dev/null 2>&1
  assert_lacks_range "$exact" "10.0.0.4/32"
  assert_has_range "$exact" "10.0.0.5/32"

  run_reconcile unchanged verify "$TEST_DIR/unused" >/dev/null 2>&1 || fail 'unchanged exact set must verify'
  assert_fails additions verify
  assert_fails removals verify
  assert_fails divergent verify
  assert_fails azure_broad verify
}

test_read_only_contract() {
  run_reconcile unchanged retained "$TEST_DIR/read-only.tfvars.json" >/dev/null 2>&1
  grep -q 'firewall-rule list' "$FAKE_CALL_LOG" || fail 'retained must read current Azure rules'
  if grep -Eq 'firewall-rule (create|update|delete)|tofu .*apply' "$FAKE_CALL_LOG"; then
    fail 'helper must never mutate rules or run apply'
  fi
}

test_declarative_parallelism_contract() {
  local output="$TEST_DIR/parallel.tfvars.json" guidance
  guidance="$(run_reconcile unchanged exact "$output" 2>&1 >/dev/null)"
  [[ "$guidance" != *-parallelism=* ]] || fail 'default guidance must preserve OpenTofu default parallelism'
  guidance="$(run_reconcile unchanged exact "$output" --parallelism 3 2>&1 >/dev/null)"
  [[ "$guidance" == *-parallelism=3* ]] || fail 'explicit parallelism override must be surfaced'
  assert_fails unchanged exact --parallelism nope

  local resource_block
  resource_block="$(sed -n '/resource "azurerm_postgresql_flexible_server_firewall_rule" "this" {/,/^}/p' "$DATABASE_MAIN")"
  [[ "$resource_block" == *'for_each = var.reviewed_egress_ranges'* ]] || fail 'firewall rules must use sibling for_each resources'
  [[ "$resource_block" != *depends_on* ]] || fail 'firewall rules must not serialize through depends_on'
}

setup
test_collection_normalization_and_guards
test_retained_exact_and_verify_transitions
test_read_only_contract
test_declarative_parallelism_contract
printf '%s\n' 'All PostgreSQL egress reconciliation tests passed.'

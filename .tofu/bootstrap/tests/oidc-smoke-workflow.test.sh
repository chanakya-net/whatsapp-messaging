#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
WORKFLOW="$ROOT_DIR/.github/workflows/oidc-smoke-test.yml"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

assert_contains() {
  grep -F -- "$2" "$1" >/dev/null || fail "missing workflow contract: $2"
}

assert_count() {
  local actual
  actual=$(grep -E -c -- "$2" "$1" || true)
  [[ "$actual" == "$3" ]] || fail "expected $3 matches for '$2'; found $actual"
}

[[ -f "$WORKFLOW" ]] || fail "OIDC smoke workflow missing"

assert_contains "$WORKFLOW" "workflow_dispatch:"
for choice in all shared dev prod; do
  assert_contains "$WORKFLOW" "- $choice"
done

assert_contains "$WORKFLOW" "permissions:"
assert_contains "$WORKFLOW" "contents: read"
assert_count "$WORKFLOW" '^[[:space:]]+id-token: write$' 3
assert_count "$WORKFLOW" '^[[:space:]]+environment: (shared|dev|prod)$' 3
assert_count "$WORKFLOW" 'uses: actions/checkout@[0-9a-f]{40}' 3
assert_count "$WORKFLOW" 'uses: Azure/login@[0-9a-f]{40}' 3

assert_count "$WORKFLOW" 'az group show --name "\$ALLOWED_RESOURCE_GROUP"' 3
assert_count "$WORKFLOW" 'az group show --name "\$DENIED_RESOURCE_GROUP"' 3
assert_count "$WORKFLOW" 'ResourceNotFound\|ResourceGroupNotFound' 3
assert_count "$WORKFLOW" 'AuthorizationFailed\|AuthorizationDenied\|Forbidden' 3
assert_count "$WORKFLOW" 'inconclusive_missing_resource' 3
assert_count "$WORKFLOW" 'authorization_denied' 3

if grep -E '(cat .*denial|ACTIONS_STEP_DEBUG|az account get-access-token|::debug::)' "$WORKFLOW" >/dev/null; then
  fail "workflow may expose denial details or credentials"
fi

printf 'PASS: OIDC smoke workflow contract\n'

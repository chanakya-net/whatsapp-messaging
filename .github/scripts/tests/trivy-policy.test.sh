#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/trivy-policy"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

finding_count() {
  jq '[.Results[]?.Vulnerabilities[]?] | length' "$1"
}

blocking_count() {
  jq '[
    .Results[]?.Vulnerabilities[]?
    | select(.Severity == "HIGH" or .Severity == "CRITICAL")
    | select((.FixedVersion // "") != "")
  ] | length' "$1"
}

policy_gate() {
  [ "$(blocking_count "$1")" -eq 0 ]
}

command -v jq >/dev/null 2>&1 || fail 'jq is required for Trivy policy fixture tests.'
for fixture in fixable-high.json unfixed-critical.json; do
  [ -f "$FIXTURES/$fixture" ] || fail "Missing Trivy fixture: $fixture"
  jq -e '.SchemaVersion and .Results' "$FIXTURES/$fixture" >/dev/null \
    || fail "Invalid Trivy fixture: $fixture"
done

[ "$(finding_count "$FIXTURES/fixable-high.json")" -eq 1 ] \
  || fail 'Fixable fixture must contain one recorded finding.'
[ "$(blocking_count "$FIXTURES/fixable-high.json")" -eq 1 ] \
  || fail 'Fixable HIGH finding must be selected by the blocking policy.'
if policy_gate "$FIXTURES/fixable-high.json"; then
  fail 'Fixable HIGH finding did not fail the policy gate.'
fi

[ "$(finding_count "$FIXTURES/unfixed-critical.json")" -eq 1 ] \
  || fail 'Unfixed fixture must contain one recorded finding.'
[ "$(blocking_count "$FIXTURES/unfixed-critical.json")" -eq 0 ] \
  || fail 'Unfixed CRITICAL finding must be excluded from the blocking policy.'
policy_gate "$FIXTURES/unfixed-critical.json" \
  || fail 'Unfixed CRITICAL finding unexpectedly failed the policy gate.'

printf '%s\n' 'Trivy policy fixture checks passed.'

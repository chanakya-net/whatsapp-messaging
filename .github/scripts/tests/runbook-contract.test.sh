#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RUNBOOKS_DIR="$REPO_ROOT/docs/runbooks"
DEPLOYMENT_GUIDE="$REPO_ROOT/docs/deployment.md"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
pass() { printf 'PASS: %s\n' "$1"; }

assert_file() {
  [[ -f "$1" ]] || fail "missing required document: $1"
}

assert_contains() {
  grep -Fq -- "$1" "$2" || fail "$3"
}

assert_no_regex() {
  local pattern=$1 path=$2 message=$3
  if grep -Eqi -- "$pattern" "$path"; then
    fail "$message ($path)"
  fi
}

all_docs() {
  printf '%s\n' "$DEPLOYMENT_GUIDE"
  find "$RUNBOOKS_DIR" -maxdepth 1 -name '*.md' -type f -print | sort
}

document_has_unsafe_secret_transport() {
  grep -Eqi '(gh auth token|authorization:[[:space:]]*bearer|\$\(cat[[:space:]]+/run/secrets|password=[^[:space:]]|docker[[:space:]].*(-e|--env).*(password|secret)|keyvault secret show.*--query[[:space:]]+value)' "$1"
}

document_has_invalid_bootstrap() {
  grep -Eq 'bootstrap\.sh[[:space:]]*$|bootstrap\.sh[[:space:]]+all|bootstrap\.sh[[:space:]]+[^[:space:]]+' "$1" &&
    ! grep -Eq 'bootstrap\.sh[[:space:]]+(plan|apply|configure-github|verify)' "$1"
}

document_has_unsupported_location_or_name() {
  grep -Eqi '(region|location)[[:space:]]*=[[:space:]]*(eastus|australiaeast)|rg-messagebridge-(dev|prod)-[a-z]+-[0-9]{3}' "$1"
}

document_has_unsafe_release_path() {
  grep -Eqi '(docker[[:space:]]+buildx[[:space:]]+build.*--push|az[[:space:]]+containerapp[[:space:]]+(update|revision[[:space:]]+activate)|az[[:space:]]+containerapp[[:space:]]+job[[:space:]]+start)' "$1" ||
    grep -Eq 'gh workflow run delivery\.yml' "$1" && ! grep -Eq 'gh workflow run delivery\.yml[[:space:]]+--ref[[:space:]]+"\$DELIVERY_REF"' "$1"
}

document_has_contradictory_delivery_guidance() {
  grep -Eqi 'sole ordered application.delivery path' "$1" &&
    grep -Eqi '(docker[[:space:]]+buildx.*--push|containerapp[[:space:]]+(update|job[[:space:]]+start))' "$1"
}

document_has_missing_mutation_field() {
  local path=$1 field
  for field in 'Target:' 'Inputs:' 'Safe path:' 'Expected result:' 'Failure interpretation:' 'Approval boundary:' 'Cleanup:'; do
    grep -Fq -- "$field" "$path" || return 0
  done
  return 1
}

test_required_documents() {
  local doc
  for doc in deployment rollback migration-failure secret-rotation cloudamqp-outage database-restore; do
    assert_file "$RUNBOOKS_DIR/$doc.md"
  done
  assert_file "$DEPLOYMENT_GUIDE"
  pass 'required deployment and runbook documents exist'
}

test_secret_safety() {
  local doc
  while IFS= read -r doc; do
    assert_no_regex '(gh auth token|authorization:[[:space:]]*bearer|\$\(cat[[:space:]]+/run/secrets|password=[^[:space:]]|docker[[:space:]].*(-e|--env).*(password|secret)|keyvault secret show.*--query[[:space:]]+value)' "$doc" 'unsafe secret transport documented'
  done < <(all_docs)
  pass 'documentation contains no token capture, secret retrieval, or secret command transport'
}

test_bootstrap_and_foundation_contract() {
  local doc="$RUNBOOKS_DIR/deployment.md"
  assert_contains 'scripts/infra/bootstrap.sh plan' "$doc" 'bootstrap plan command missing'
  assert_contains 'scripts/infra/bootstrap.sh apply' "$doc" 'bootstrap apply command missing'
  assert_contains 'scripts/infra/bootstrap.sh configure-github' "$doc" 'GitHub configuration command missing'
  assert_contains '.tofu/envs/shared' "$doc" 'shared foundation root missing'
  assert_contains '.tofu/envs/dev' "$doc" 'dev foundation root missing'
  assert_contains '.tofu/envs/prod' "$doc" 'prod foundation root missing'
  assert_contains 'centralindia' "$doc" 'supported location missing'
  assert_contains 'cin' "$doc" 'supported name token missing'
  pass 'bootstrap subcommands, supported location, and foundation roots are explicit'
}

test_hitl_stages_and_links() {
  local doc="$RUNBOOKS_DIR/deployment.md" stage link
  for stage in {1..12}; do
    assert_contains "Stage $stage" "$doc" "HITL stage $stage missing"
  done
  for link in rollback migration-failure secret-rotation cloudamqp-outage database-restore; do
    assert_contains "./$link.md" "$doc" "required runbook link missing: $link"
  done
  pass 'all twelve HITL stages and recovery links are present'
}

test_delivery_contract() {
  local doc="$RUNBOOKS_DIR/deployment.md"
  assert_contains 'gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=publish -f environment=none' "$doc" 'publish dispatch missing explicit ref'
  assert_contains 'gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=dev -f environment=none' "$doc" 'dev dispatch missing explicit ref'
  assert_contains 'gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=prod -f environment=none' "$doc" 'prod dispatch missing explicit ref'
  assert_no_regex 'docker[[:space:]]+buildx[[:space:]]+build.*--push|containerapp[[:space:]]+job[[:space:]]+start|containerappsjob-' "$doc" 'runbook bypasses the protected delivery workflow'
  pass 'first release uses delivery ref, dev-to-prod handoff, and protected migration path'
}

test_mutation_metadata() {
  local doc
  while IFS= read -r doc; do
    if document_has_missing_mutation_field "$doc"; then
      fail "mutation contract fields missing from $doc"
    fi
  done < <(all_docs)
  pass 'every in-scope document supplies the HITL mutation contract fields'
}

test_no_direct_production_mutations() {
  local doc
  for doc in "$RUNBOOKS_DIR/cloudamqp-outage.md" "$RUNBOOKS_DIR/rollback.md"; do
    assert_no_regex 'az[[:space:]]+containerapp[[:space:]]+(update|revision[[:space:]]+activate)|rg-messagebridge-prod-centralindia-[0-9]{3}' "$doc" 'direct production Container Apps mutation or hard-coded target documented'
  done
  pass 'outage and rollback route production mutations through protected delivery'
}

assert_fixture_rejected() {
  local label=$1 content=$2 checker=$3 fixture
  fixture="$(mktemp)"
  printf '%s\n' "$content" >"$fixture"
  if ! "$checker" "$fixture"; then
    rm -f "$fixture"
    fail "negative fixture accepted: $label"
  fi
  rm -f "$fixture"
  pass "negative fixture rejected: $label"
}

test_negative_fixtures() {
  assert_fixture_rejected 'token capture' 'gh auth token | curl -H "Authorization: Bearer $TOKEN"' document_has_unsafe_secret_transport
  assert_fixture_rejected 'secret command substitution' 'docker run -e "Password=$(cat /run/secrets/value)" image' document_has_unsafe_secret_transport
  assert_fixture_rejected 'invalid bootstrap subcommand' 'bash scripts/infra/bootstrap.sh bootstrap' document_has_invalid_bootstrap
  assert_fixture_rejected 'unsupported location' 'location=eastus' document_has_unsupported_location_or_name
  assert_fixture_rejected 'guessed resource target' 'rg-messagebridge-prod-eus-042' document_has_unsupported_location_or_name
  assert_fixture_rejected 'unsafe direct release' 'az containerapp job start --name guessed' document_has_unsafe_release_path
  assert_fixture_rejected 'unreferenced dispatch' 'gh workflow run delivery.yml -f target=dev -f environment=none' document_has_unsafe_release_path
  assert_fixture_rejected 'missing mutation field' $'Target: x\nInputs: x\nSafe path: x' document_has_missing_mutation_field
  assert_fixture_rejected 'contradictory delivery guidance' $'delivery.yml is the sole ordered application-delivery path\ndocker buildx build --push' document_has_contradictory_delivery_guidance
}

main() {
  test_required_documents
  test_secret_safety
  test_bootstrap_and_foundation_contract
  test_hitl_stages_and_links
  test_delivery_contract
  test_mutation_metadata
  test_no_direct_production_mutations
  test_negative_fixtures
  pass 'runbook contract tests passed'
}

main "$@"

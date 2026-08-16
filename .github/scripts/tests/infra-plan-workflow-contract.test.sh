#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORKFLOW="$REPO_ROOT/.github/workflows/infra-plan.yml"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/infra-plan"
REQUESTED_CASE="${1:---case=all}"
REQUESTED_CASE="${REQUESTED_CASE#--case=}"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

job_block() {
  local job="$1"
  awk -v job="$job" '
    $0 == "  " job ":" { found = 1 }
    found && $0 ~ /^  [a-zA-Z0-9_-]+:$/ && $0 != "  " job ":" { exit }
    found { print }
  ' "$WORKFLOW"
}

job_permissions() {
  job_block "$1" | awk '
    /^    permissions:$/ { found = 1 }
    found && /^    [a-zA-Z0-9_-]+:/ && !/^    permissions:$/ { exit }
    found { print }
  '
}

top_permissions() {
  awk '
    /^permissions:$/ { found = 1 }
    found && /^[a-zA-Z0-9_-]+:$/ && !/^permissions:$/ { exit }
    found { print }
  ' "$WORKFLOW"
}

assert_contains() {
  local text="$1" pattern="$2" message="$3"
  grep -Eq -- "$pattern" <<<"$text" || fail "$message"
}

assert_absent() {
  local text="$1" pattern="$2" message="$3"
  if grep -Eq -- "$pattern" <<<"$text"; then
    fail "$message"
  fi
}

offline_pr_contract() {
  [[ -f "$WORKFLOW" ]] || fail 'infra-plan workflow missing'
  local workflow offline
  workflow="$(<"$WORKFLOW")"
  offline="$(job_block offline-checks)"
  [[ -n "$offline" ]] || fail 'offline-checks job missing'

  assert_contains "$workflow" '^  pull_request:$' 'workflow must use pull_request'
  assert_absent "$workflow" 'pull_request_target' 'pull_request_target is forbidden'
  for path in '\.tofu/\*\*' 'scripts/infra/\*\*' 'scripts/db/\*\*' \
    '\.github/workflows/infra-plan\.yml' '\.github/scripts/upsert-infra-plan-comment\.sh' \
    '\.github/scripts/tests/infra-plan-workflow-contract\.test\.sh' \
    '\.github/scripts/tests/upsert-infra-plan-comment\.test\.sh'; do
    assert_contains "$workflow" "$path" "pull_request paths must include $path"
  done
  assert_contains "$workflow" 'infra-plan-\$\{\{ github\.event\.pull_request\.number \}\}' \
    'concurrency must be scoped to PR number'
  assert_contains "$workflow" '^  cancel-in-progress: true$' 'superseded plans must cancel'
  [[ "$(top_permissions)" == $'permissions:\n  contents: read' ]] ||
    fail 'top-level permissions must grant contents read only'

  [[ "$(job_permissions offline-checks)" == $'    permissions:\n      contents: read' ]] ||
    fail 'offline job permissions must grant contents read only'
  assert_absent "$offline" 'id-token:|pull-requests:|secrets\.|AZURE_|azure/login|tofu plan' \
    'offline job must not use OIDC, writes, Azure, secrets, or remote planning'
  assert_contains "$offline" 'tofu fmt -check -recursive \.tofu' 'offline job must check formatting'
  assert_contains "$offline" 'find \.tofu -name versions\.tf' 'offline job must discover every OpenTofu root'
  assert_contains "$offline" 'init -backend=false -input=false' 'offline init must disable backends'
  assert_contains "$offline" 'tofu -chdir="\$root" validate' 'offline job must validate every root'
  assert_contains "$offline" 'tofu -chdir="\$root" test' 'offline job must test every root'
  assert_contains "$offline" 'find \.tofu -path .\*/tests/\*.test\.sh' \
    'offline job must discover shell OpenTofu contracts'
  assert_contains "$offline" 'infra-plan-workflow-contract\.test\.sh' \
    'offline job must run workflow security tests'
  assert_contains "$offline" 'upsert-infra-plan-comment\.test\.sh' \
    'offline job must run comment helper tests'

  local action_count pinned_count
  action_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@' <<<"$offline")"
  pinned_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@[0-9a-f]{40}([[:space:]]|$)' <<<"$offline")"
  [[ "$action_count" -eq "$pinned_count" ]] || fail 'offline actions must use full commit pins'
}

trusted_plan_contract() {
  [[ -f "$WORKFLOW" ]] || fail 'infra-plan workflow missing'
  local workflow plan
  workflow="$(<"$WORKFLOW")"
  plan="$(job_block trusted-plan)"
  [[ -n "$plan" ]] || fail 'trusted-plan job missing'

  assert_contains "$plan" '^    needs: offline-checks$' 'plans must wait for offline checks'
  assert_contains "$plan" 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository' \
    'plan must require same-repository head'
  assert_contains "$plan" 'github\.event\.pull_request\.base\.repo\.full_name == github\.repository' \
    'plan must require same-repository base'
  [[ "$(job_permissions trusted-plan)" == $'    permissions:\n      contents: read\n      id-token: write' ]] ||
    fail 'plan job permissions must grant contents read and OIDC only'
  assert_absent "$plan" 'pull-requests: write|secrets\.|continue-on-error|always\(\)' \
    'plan job must not receive PR writes, secrets, or failure masking'
  assert_contains "$plan" 'AZURE_CLIENT_ID_PLAN' 'plan job must use bootstrap plan identity'
  assert_contains "$plan" 'Azure/login@[0-9a-f]{40}' 'plan job must use pinned Azure login'
  assert_contains "$plan" 'matrix:' 'plan job must use a layer matrix'
  for layer in shared dev prod; do
    assert_contains "$plan" "layer: $layer" "plan matrix must include $layer"
  done
  assert_absent "$plan" 'layer: bootstrap' 'bootstrap remote state must not be planned'
  for layer_upper in SHARED DEV PROD; do
    assert_contains "$plan" "TOFU_PLAN_VARS_$layer_upper:.*vars\\.TOFU_PLAN_VARS_$layer_upper" \
      "plan input must come from TOFU_PLAN_VARS_$layer_upper"
  done
  assert_contains "$plan" 'type == "object"' 'plan variables must be validated as JSON objects'
  assert_contains "$plan" 'secret\|token\|password\|credential\|connection' \
    'secret-named plan inputs must be rejected'
  assert_contains "$plan" 'messagebridgetfstate\(\[0-9\]\{3\}\)' \
    'bootstrap serial must be derived from validated state account name'
  assert_contains "$plan" 'repository: \$repository' 'repository must be injected into plan variables'
  assert_contains "$plan" 'init -reconfigure -input=false' 'remote backend must be initialized explicitly'
  assert_contains "$plan" 'use_azuread_auth=true' 'backend must use Azure AD authentication'
  assert_contains "$plan" 'plan -input=false -lock=false -detailed-exitcode' \
    'plan must be read-only, unlocked, and use detailed exit codes'
  assert_contains "$plan" 'plan_exit -ne 0 && \$plan_exit -ne 2' \
    'only plan exit codes zero and two may pass'
  assert_absent "$plan" 'tofu[^\n]*(apply|import|destroy|state (push|mv|rm))' \
    'workflow must never mutate infrastructure or state'

  local action_count pinned_count
  action_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@' <<<"$plan")"
  pinned_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@[0-9a-f]{40}([[:space:]]|$)' <<<"$plan")"
  [[ "$action_count" -eq "$pinned_count" ]] || fail 'plan actions must use full commit pins'

  jq -e '.repository == .pull_request.head.repo.full_name and
    .repository == .pull_request.base.repo.full_name' "$FIXTURES/trusted-pr.json" >/dev/null ||
    fail 'trusted fixture must satisfy exact same-repository gate'
  if jq -e '.repository == .pull_request.head.repo.full_name and
    .repository == .pull_request.base.repo.full_name' "$FIXTURES/fork-pr.json" >/dev/null; then
    fail 'fork fixture must fail exact same-repository gate'
  fi
}

sanitized_comment_handoff_contract() {
  [[ -f "$WORKFLOW" ]] || fail 'infra-plan workflow missing'
  local plan comments
  plan="$(job_block trusted-plan)"
  comments="$(job_block plan-comments)"
  [[ -n "$comments" ]] || fail 'plan-comments job missing'

  assert_contains "$plan" 'tofu -chdir="\$ROOT" show -json "\$RUNTIME_DIR/plan\.bin"' \
    'saved plan must be converted to JSON without printing it'
  assert_contains "$plan" 'upsert-infra-plan-comment\.sh"? render "\$LAYER"' \
    'trusted job must render a sanitized summary'
  assert_contains "$plan" 'trap .*rm -rf.*RUNTIME_DIR.* EXIT' \
    'trusted job must remove raw logs, variables, JSON, and saved plan'
  assert_contains "$plan" 'actions/upload-artifact@[0-9a-f]{40}' \
    'sanitized summary must use a pinned artifact upload'
  assert_contains "$plan" 'name: infra-plan-summary-\$\{\{ matrix\.layer \}\}' \
    'artifact must be separated by layer'
  assert_contains "$plan" 'retention-days: [123]$' 'sanitized artifact retention must be short'
  assert_absent "$plan" 'path:.*(plan\.bin|plan\.json|plan\.log|tfvars)' \
    'raw plans, logs, and variables must never be uploaded'

  assert_contains "$comments" '^    needs: trusted-plan$' 'comments must wait for every trusted plan'
  assert_contains "$comments" 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository' \
    'comments must require same-repository head'
  assert_contains "$comments" 'github\.event\.pull_request\.base\.repo\.full_name == github\.repository' \
    'comments must require same-repository base'
  [[ "$(job_permissions plan-comments)" == $'    permissions:\n      contents: read\n      pull-requests: write' ]] ||
    fail 'comment job permissions must grant contents read and PR write only'
  assert_absent "$comments" 'id-token:|azure/login|AZURE_|ARM_|TOFU_PLAN_VARS_|secrets\.|tofu (init|plan|show)|continue-on-error|always\(\)' \
    'comment job must not receive OIDC, Azure, plan inputs, secrets, raw plans, or failure masking'
  assert_contains "$comments" 'actions/download-artifact@[0-9a-f]{40}' \
    'comment job must use a pinned artifact download'
  assert_contains "$comments" 'pattern: infra-plan-summary-\*' \
    'comment job must download only sanitized summaries'
  assert_contains "$comments" 'for layer in shared dev prod' 'comment job must process every supported layer'
  assert_contains "$comments" 'upsert-infra-plan-comment\.sh"? upsert "\$layer"' \
    'comment job must use deterministic helper upserts'

  local action_count pinned_count
  action_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@' <<<"$comments")"
  pinned_count="$(grep -Ec '^[[:space:]]+uses: [^ ]+@[0-9a-f]{40}([[:space:]]|$)' <<<"$comments")"
  [[ "$action_count" -eq "$pinned_count" ]] || fail 'comment actions must use full commit pins'
}

case "$REQUESTED_CASE" in
  offline_pr_contract) offline_pr_contract ;;
  trusted_plan_contract) trusted_plan_contract ;;
  sanitized_comment_handoff_contract) sanitized_comment_handoff_contract ;;
  all)
    offline_pr_contract
    trusted_plan_contract
    sanitized_comment_handoff_contract
    ;;
  *) fail "unknown case: $REQUESTED_CASE" ;;
esac

printf 'PASS: infra plan workflow contract %s\n' "$REQUESTED_CASE"

#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORKFLOW="$REPO_ROOT/.github/workflows/delivery.yml"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/delivery/cases.json"
PUBLISH_WORKFLOW="$REPO_ROOT/.github/workflows/_publish-images.yml"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
assert_contains() { grep -Eq -- "$2" <<<"$1" || fail "$3"; }
assert_absent() { ! grep -Eqi -- "$2" <<<"$1" || fail "$3"; }

job_block() {
  awk -v job="$1" '
    $0 == "  " job ":" { found = 1 }
    found && $0 ~ /^  [a-zA-Z0-9_-]+:$/ && $0 != "  " job ":" { exit }
    found { print }
  ' "$WORKFLOW"
}

step_script() {
  awk -v name="$1" '
    $0 == "      - name: " name { found = 1; next }
    found && /^        run: \|$/ { body = 1; next }
    body && $0 !~ /^          / && $0 !~ /^$/ { exit }
    body { sub(/^          /, ""); print }
  ' "$WORKFLOW"
}

test_dispatch_input_and_routing_contract() {
  local triggers changes classifier input_names test_dir case_json name target environment passes exit_code key expected actual
  triggers="$(sed -n '/^  workflow_dispatch:/,/^permissions:/p' "$WORKFLOW")"
  changes="$(job_block changes)"
  assert_contains "$triggers" '^  workflow_dispatch:$' 'Manual dispatch trigger must exist.'
  for input in target environment; do
    assert_contains "$triggers" "^      $input:" "Dispatch must define $input input."
    assert_contains "$triggers" "^        type: choice$" "$input must be a bounded choice."
    assert_contains "$triggers" "^        required: true$" "$input must be required."
  done
  for target in publish dev prod reload-secrets; do
    assert_contains "$triggers" "^          - $target$" "Missing dispatch target $target."
  done
  for environment in none dev prod; do
    assert_contains "$triggers" "^          - $environment$" "Missing dispatch environment $environment."
  done
  assert_contains "$triggers" '^        default: publish$' 'Publish must be least-privileged target default.'
  assert_contains "$triggers" '^        default: none$' 'No environment must be default.'
  input_names="$(awk '/^      [a-zA-Z0-9_-]+:$/ { sub(/^      /, ""); sub(/:$/, ""); print }' <<<"$triggers")"
  [[ "$input_names" == $'target\nenvironment' ]] || fail 'Dispatch must expose only target and environment inputs.'
  assert_absent "$input_names" 'digest|tag|secret|token|password|credential|connection|key.?vault|value' \
    'Dispatch must not accept digest or secret-bearing inputs.'
  for output in delivery-target delivery-environment; do
    assert_contains "$changes" "^      $output:" "Classifier must expose sanitized $output."
  done
  [[ "$(grep -c 'github\.event\.inputs\.' "$WORKFLOW")" == 2 ]] || \
    fail 'Only the fail-closed classifier may read raw dispatch inputs.'

  classifier="$(step_script 'Classify changed delivery layers')"
  [ -n "$classifier" ] || fail 'Classification script missing.'
  assert_contains "$classifier" 'delivery_target=automatic delivery_environment=none' \
    'Push delivery must retain a bounded automatic route.'
  test_dir="$(mktemp -d)"
  trap 'rm -rf -- "$test_dir"' RETURN
  while IFS= read -r case_json; do
    name="$(jq -r '.name' <<<"$case_json")"
    target="$(jq -r '.target' <<<"$case_json")"
    environment="$(jq -r '.environment' <<<"$case_json")"
    passes="$(jq -r '.passes' <<<"$case_json")"
    : >"$test_dir/$name.out"
    exit_code=0
    EVENT_NAME=workflow_dispatch DELIVERY_TARGET="$target" DELIVERY_ENVIRONMENT="$environment" \
      GITHUB_OUTPUT="$test_dir/$name.out" RUNNER_TEMP="$test_dir" \
      bash -Eeuo pipefail -c "$classifier" >/dev/null 2>&1 || exit_code=$?
    if [[ "$passes" == true ]]; then
      [[ "$exit_code" == 0 ]] || fail "$name dispatch route unexpectedly failed."
      for key in delivery-target delivery-environment application infrastructure shared dev prod layers; do
        expected="$(jq -r --arg key "$key" '.expected[$key]' <<<"$case_json")"
        actual="$(sed -n "s/^$key=//p" "$test_dir/$name.out")"
        [[ "$actual" == "$expected" ]] || fail "$name $key: expected $expected, got $actual"
      done
    else
      [[ "$exit_code" != 0 ]] || fail "$name dispatch route must fail closed."
      [[ ! -s "$test_dir/$name.out" ]] || fail "$name must not emit authorization outputs."
    fi
  done < <(jq -c '.dispatch_routing[]' "$FIXTURES")
}

test_publish_target_scope() {
  local workflow validation images application dev prod job block publish_workflow
  workflow="$(cat "$WORKFLOW")"
  validation="$(job_block validation)"
  images="$(job_block publish-images)"
  application="$(job_block application-ready)"
  dev="$(job_block dev-release)"
  prod="$(job_block prod-release)"
  publish_workflow="$(cat "$PUBLISH_WORKFLOW")"

  assert_contains "$validation" 'uses: \./\.github/workflows/_validation\.yml' \
    'Manual publication must reuse delivery validation.'
  assert_contains "$images" 'uses: \./\.github/workflows/_publish-images\.yml' \
    'Manual publication must reuse verified image publication.'
  assert_contains "$images" "delivery-target.*reload-secrets" \
    'Publication gate must use the sanitized dispatch target.'
  assert_contains "$images" '^      packages: write$' 'Only image publication needs package write.'
  [[ "$(grep -c '^      packages: write$' <<<"$workflow")" == 1 ]] || \
    fail 'Package write must be granted only to image publication.'
  assert_contains "$application" 'WORKER_DIGEST:.*needs\.publish-images\.outputs\.worker-digest' \
    'Worker digest must flow through the existing readiness gate.'
  assert_contains "$application" 'MIGRATE_DIGEST:.*needs\.publish-images\.outputs\.migrate-digest' \
    'Migration digest must flow through the existing readiness gate.'
  assert_contains "$publish_workflow" 'value:.*jobs\.verify\.outputs\.worker-digest' \
    'Worker digest must originate from anonymous verification.'
  assert_contains "$publish_workflow" 'value:.*jobs\.verify\.outputs\.migrate-digest' \
    'Migration digest must originate from anonymous verification.'
  for target in automatic dev prod; do
    assert_contains "$dev" "delivery-target.*'$target'" \
      "Development release must allow sanitized $target routing."
  done
  assert_absent "$dev" "delivery-target.*== 'publish'" \
    'Development release must exclude manual publish.'
  assert_contains "$prod" "delivery-target.*prod" \
    'Production release must require automatic delivery or manual prod.'
  for job in shared-retained dev-infrastructure prod-infrastructure shared-reconcile; do
    block="$(job_block "$job")"
    assert_contains "$block" "needs\.changes\.outputs\.infrastructure == 'true'" \
      "$job must remain unreachable when manual routing marks infrastructure false."
  done
}

test_dev_target_reuses_release() {
  local dev prod
  dev="$(job_block dev-release)"
  prod="$(job_block prod-release)"
  assert_contains "$dev" "delivery-target.*'dev'" 'Manual dev must route to the existing development release.'
  assert_contains "$dev" '^    needs: \[changes, validation, application-ready, infrastructure-complete\]$' \
    'Manual dev must retain validated readiness dependencies.'
  assert_contains "$dev" '^    environment: dev$' 'Manual dev must retain development approval boundary.'
  assert_contains "$dev" 'MIGRATE_DIGEST:.*needs\.application-ready\.outputs\.migrate-digest' \
    'Manual dev migration must use the gated verified digest.'
  assert_contains "$dev" 'WORKER_DIGEST:.*needs\.application-ready\.outputs\.worker-digest' \
    'Manual dev worker must use the gated verified digest.'
  assert_contains "$dev" 'Migrate, deploy, smoke, and roll back development' \
    'Manual dev must reuse migration-before-worker, smoke, and rollback behavior.'
  assert_absent "$dev" 'needs\.publish-images\.outputs|:latest|workflow_dispatch.*run:' \
    'Manual dev must not bypass readiness or use a mutable image.'
  assert_absent "$prod" "delivery-target.*== 'dev'" 'Manual dev must not reach production.'
}

test_prod_target_reuses_protected_promotion() {
  local dev prod handoff_line login_line
  dev="$(job_block dev-release)"
  prod="$(job_block prod-release)"
  assert_contains "$dev" "delivery-target.*'prod'" 'Manual prod must first route through development.'
  assert_contains "$prod" "delivery-target.*'prod'" 'Manual prod must route to existing promotion.'
  assert_contains "$prod" "dev-release\.result == 'success'" \
    'Manual prod must require successful development verification.'
  assert_contains "$prod" '^    environment: prod$' 'Manual prod must retain protected production approval.'
  assert_contains "$prod" '^      group: production-promotion$' \
    'Manual prod must retain non-cancelling production concurrency.'
  assert_contains "$prod" 'name: Validate exact development promotion handoff' \
    'Manual prod must validate the immutable development handoff.'
  assert_contains "$prod" 'WORKER_DIGEST:.*steps\.handoff\.outputs\.worker-digest' \
    'Production worker digest must come from the validated handoff.'
  assert_contains "$prod" 'MIGRATE_DIGEST:.*steps\.handoff\.outputs\.migrate-digest' \
    'Production migration digest must come from the validated handoff.'
  handoff_line="$(grep -n 'name: Validate exact development promotion handoff' <<<"$prod" | cut -d: -f1)"
  login_line="$(grep -n 'name: Sign in with production deploy identity' <<<"$prod" | cut -d: -f1)"
  ((handoff_line < login_line)) || fail 'Production handoff validation must precede production OIDC.'
  assert_absent "$prod" 'needs\.(publish-images|application-ready)|:latest|buildx|build-push' \
    'Production must neither rebuild nor bypass tested digests.'
}

count_calls() { grep -Ec -- "$2" "$1" 2>/dev/null || true; }

assert_reload_job() {
  local environment=$1 client=$2 block
  block="$(job_block "$environment-secret-reload")"
  [ -n "$block" ] || fail "Missing $environment secret reload job."
  assert_contains "$block" "delivery-target.*'reload-secrets'" "$environment reload needs sanitized target gate."
  assert_contains "$block" "delivery-environment.*'$environment'" "$environment reload needs sanitized environment gate."
  assert_contains "$block" "^    environment: $environment$" "$environment reload needs protected environment."
  assert_contains "$block" '^      id-token: write$' "$environment reload needs scoped OIDC."
  assert_contains "$block" "AZURE_CLIENT_ID_$client" "$environment reload must use its own identity."
  if [[ "$environment" == dev ]]; then
    assert_absent "$block" 'AZURE_CLIENT_ID_PROD|TOFU_STATE_(CONTAINER|KEY)_PROD' 'Dev reload received prod access.'
  else
    assert_absent "$block" 'AZURE_CLIENT_ID_DEV|TOFU_STATE_(CONTAINER|KEY)_DEV' 'Prod reload received dev access.'
    assert_contains "$block" '^      group: production-promotion$' 'Prod reload must serialize production mutation.'
  fi
  assert_absent "$block" 'packages: write|migration-job|MIGRATE_DIGEST|containerapp job update' \
    "$environment reload must neither publish nor migrate."
  assert_contains "$block" 'tofu .* output -raw worker_name' "$environment reload must resolve worker from state."
  assert_contains "$block" 'tofu .* output -raw smoke_job_name' "$environment reload must resolve smoke job from state."
  assert_contains "$block" 'properties\.configuration\.secrets' "$environment reload must inspect secret-reference metadata."
  assert_contains "$block" 'keyVaultUrl' "$environment reload must validate versionless Key Vault URLs."
  assert_contains "$block" 'wait-for-container-app-revision\.sh' "$environment reload must use bounded revision waits."
  assert_contains "$block" 'wait-for-container-app-job\.sh' "$environment reload must use bounded smoke waits."
  assert_contains "$block" 'containerapp revision activate' "$environment reload must reactivate prior revision on failure."
  assert_absent "$block" 'cat .*metadata|tee .*metadata|set -x|--secrets|secret-value' \
    "$environment reload must not print or replace secret values."
}

run_reload_case() {
  local script=$1 test_dir=$2 case_json=$3 environment=$4 name output calls state log exit_code=0 key expected actual
  name="$(jq -r '.name' <<<"$case_json")"
  output="$test_dir/$environment-$name.out"; calls="$test_dir/$environment-$name.calls"
  state="$test_dir/$environment-$name-state"; log="$test_dir/$environment-$name.log"
  mkdir -p "$state"; : >"$output"; : >"$calls"
  PATH="$test_dir/bin:$PATH" FAKE_AZ_CASE="$case_json" FAKE_AZ_CALL_LOG="$calls" \
    FAKE_AZ_STATE_DIR="$state" GITHUB_OUTPUT="$output" RUNNER_TEMP="$test_dir" \
    GITHUB_RUN_ID=123 GITHUB_RUN_ATTEMPT=4 ARM_SUBSCRIPTION_ID=00000000-0000-4000-8000-000000000002 \
    RELOAD_ENVIRONMENT="$environment" WORKER_NAME="ca-messagebridge-$environment-cin-042" \
    SMOKE_JOB_NAME="smoke-messagebridge-$environment-cin-042" \
    RESOURCE_GROUP="rg-messagebridge-$environment-centralindia-042" \
    REVISION_POLL_BUDGET=1 REVISION_TIMEOUT=10 SMOKE_POLL_BUDGET=1 SMOKE_TIMEOUT=10 \
    bash -Eeuo pipefail -c "$script" >"$log" 2>&1 || exit_code=$?
  expected="$(jq -r '.expected.exit' <<<"$case_json")"
  [[ "$exit_code" == "$expected" ]] || fail "$environment/$name exit: expected $expected, got $exit_code"
  for key in current-digest execution-result health-result smoke-result rollback-result; do
    expected="$(jq -r --arg key "$key" '.expected[$key]' <<<"$case_json")"
    actual="$(sed -n "s/^$key=//p" "$output" | tail -1)"
    [[ "$actual" == "$expected" ]] || fail "$environment/$name $key: expected $expected, got $actual"
  done
  for key in updates activations smoke-starts; do
    expected="$(jq -r --arg key "$key" '.expected[$key]' <<<"$case_json")"
    actual="$(count_calls "$calls" "^containerapp ${key/updates/update}")"
    [[ "$key" != activations ]] || actual="$(count_calls "$calls" '^containerapp revision activate')"
    [[ "$key" != smoke-starts ]] || actual="$(count_calls "$calls" '^containerapp job start')"
    [[ "$actual" == "$expected" ]] || fail "$environment/$name $key: expected $expected, got $actual"
  done
  assert_absent "$(<"$log")" 'fixture-secret-value' "$environment/$name leaked fixture secret value."
  assert_absent "$(<"$calls")" 'fixture-secret-value|containerapp job update|migrate' "$environment/$name used unsafe command data."
  if [[ "$(jq -r '.expected["rollback-result"]' <<<"$case_json")" == success ]]; then
    assert_contains "$(<"$calls")" 'containerapp revision activate.*--revision .*--prior' \
      "$environment/$name must reactivate captured prior revision."
  fi
}

test_secret_reload_scenarios() {
  local fixture test_dir environment script case_json
  assert_reload_job dev DEV
  assert_reload_job prod PROD
  fixture="$REPO_ROOT/.github/scripts/tests/fixtures/delivery/reload/fake-az.sh"
  [ -f "$fixture" ] || fail 'Secret reload Azure fixture missing.'
  test_dir="$(mktemp -d)"; trap 'rm -rf -- "$test_dir"' RETURN
  mkdir -p "$test_dir/bin"; install -m 700 "$fixture" "$test_dir/bin/az"
  for environment in dev prod; do
    script="$(step_script "Reload $environment secrets and verify rollback")"
    [ -n "$script" ] || fail "$environment reload script missing."
    while IFS= read -r case_json; do
      run_reload_case "$script" "$test_dir" "$case_json" "$environment"
    done < <(jq -c '.reload[]' "$FIXTURES")
  done
}

assert_manual_summary_contract() {
  local summary=$1 script=$2 manual manual_output job field
  assert_contains "$summary" 'needs:.*dev-secret-reload.*prod-secret-reload' \
    'Summary must wait for both bounded reload jobs.'
  assert_contains "$summary" 'EVENT_NAME:.*github\.event_name' 'Summary must distinguish automatic and manual delivery.'
  assert_contains "$summary" 'DELIVERY_TARGET:.*needs\.changes\.outputs\.delivery-target' \
    'Summary must use only sanitized target routing.'
  assert_contains "$summary" 'DELIVERY_ENVIRONMENT:.*needs\.changes\.outputs\.delivery-environment' \
    'Summary must use only sanitized environment routing.'
  for job in DEV PROD; do
    assert_contains "$summary" "${job}_RELOAD_DIGEST:.*secret-reload\.outputs\.current-digest" \
      'Summary must receive only captured reload digest/status outputs.'
    assert_contains "$summary" "${job}_RELOAD_ROLLBACK:.*secret-reload\.outputs\.rollback-result" \
      'Summary must receive verified reload rollback status.'
  done
  manual="$(awk '
    /^if \[\[ \"\$EVENT_NAME\" == workflow_dispatch \]\]; then$/ { found = 1 }
    found { print }
    found && /^  exit 0$/ { exit }
  ' <<<"$script")"
  [ -n "$manual" ] || fail 'Manual summary branch missing.'
  for field in environment digest execution_status health_status smoke_status rollback_status; do
    assert_contains "$manual" "$field:" "Manual summary must publish bounded $field."
  done
  manual_output="$(awk '/printf '\''### Manual delivery/{ found = 1 } found { print }' <<<"$manual")"
  assert_absent "$manual_output" 'COMMIT_SHA|CHANGED_LAYERS|SHARED_|DEV_PLAN|PROD_PLAN|TOFU_|AZURE_|resource|vault|secret|backend|tfstate|tfvars|cat ' \
    'Manual summary must omit commits, infrastructure, secret metadata, and backend details.'
}

run_manual_summary_cases() {
  local script=$1 test_dir target environment expected_environment output
  test_dir="$(mktemp -d)"
  while read -r target environment expected_environment; do
    output="$test_dir/$target-$environment.md"; : >"$output"
    EVENT_NAME=workflow_dispatch DELIVERY_TARGET="$target" DELIVERY_ENVIRONMENT="$environment" \
      GITHUB_STEP_SUMMARY="$output" IMAGES_RESULT=success DEV_RELEASE_JOB_RESULT=success \
      PROD_RELEASE_JOB_RESULT=success DEV_WORKER_DIGEST="$(printf 'a%.0s' {1..64})" \
      DEV_MIGRATION_DIGEST="$(printf 'b%.0s' {1..64})" PROD_WORKER_DIGEST="$(printf 'a%.0s' {1..64})" \
      PROD_MIGRATION_DIGEST="$(printf 'b%.0s' {1..64})" DEV_HEALTH_RESULT=success DEV_SMOKE_RESULT=success \
      DEV_ROLLBACK_RESULT=skipped PROD_HEALTH_RESULT=success PROD_SMOKE_RESULT=success PROD_ROLLBACK_RESULT=skipped \
      DEV_RELOAD_DIGEST="sha256:$(printf 'c%.0s' {1..64})" DEV_RELOAD_EXECUTION=success \
      DEV_RELOAD_HEALTH=success DEV_RELOAD_SMOKE=success DEV_RELOAD_ROLLBACK=skipped \
      PROD_RELOAD_DIGEST="sha256:$(printf 'c%.0s' {1..64})" PROD_RELOAD_EXECUTION=success \
      PROD_RELOAD_HEALTH=success PROD_RELOAD_SMOKE=success PROD_RELOAD_ROLLBACK=skipped \
      bash -Eeuo pipefail -c "$script"
    assert_contains "$(<"$output")" "environment:.*$expected_environment" "$target summary environment mismatch."
    [[ "$(grep -Ec '^- (environment|digest|execution_status|health_status|smoke_status|rollback_status):' "$output")" == 6 ]] ||
      fail "$target summary contains missing or extra result fields."
    assert_absent "$(<"$output")" 'commit|layer|plan|apply|resource|vault|secret|backend|tfstate|tfvars' \
      "$target summary exposed forbidden context."
  done <<'EOF'
publish none none
dev none dev
prod none prod
reload-secrets dev dev
reload-secrets prod prod
EOF
}

assert_manual_documentation() {
  local docs=$1 workflow_contract=$2 invocation
  assert_contains "$workflow_contract" 'delivery-dispatch-contract\.test\.sh' \
    'Automatic delivery contract chain must invoke focused dispatch checks.'

  for invocation in \
    'target=publish -f environment=none' \
    'target=dev -f environment=none' \
    'target=prod -f environment=none' \
    'target=reload-secrets -f environment=dev' \
    'target=reload-secrets -f environment=prod'; do
    assert_contains "$docs" "gh workflow run delivery\.yml.*$invocation" "Missing operator invocation: $invocation"
  done
  assert_contains "$docs" 'required reviewers' 'Production operations must document required approval.'
  assert_contains "$docs" 'out of band' 'Secret values must be updated in Key Vault out of band.'
  assert_contains "$docs" 'never.*pass.*secret.*workflow' 'Docs must prohibit secret workflow inputs.'
  assert_contains "$docs" 'public.*GHCR|GHCR.*public' 'Anonymous digest verification prerequisite must be documented.'
  assert_contains "$docs" 'reload rollback.*prior revision|prior revision.*reload rollback' \
    'Docs must explain verified prior-revision restoration.'
  assert_contains "$docs" 'never reverses.*database schema|database schema.*never reverses' \
    'Docs must explain schema rollback boundary.'
}

test_manual_summary_and_documentation() {
  local summary script docs workflow_contract
  summary="$(job_block delivery-summary)"
  script="$(step_script 'Summarize delivery results')"
  docs="$(cat "$REPO_ROOT/docs/deployment.md")"
  workflow_contract="$(cat "$REPO_ROOT/.github/scripts/tests/delivery-workflow-contract.test.sh")"
  assert_manual_summary_contract "$summary" "$script"
  run_manual_summary_cases "$script"
  assert_manual_documentation "$docs" "$workflow_contract"
}

test_dispatch_input_and_routing_contract
test_publish_target_scope
test_dev_target_reuses_release
test_prod_target_reuses_protected_promotion
test_secret_reload_scenarios
test_manual_summary_and_documentation
printf '%s\n' 'Delivery dispatch contract checks passed.'

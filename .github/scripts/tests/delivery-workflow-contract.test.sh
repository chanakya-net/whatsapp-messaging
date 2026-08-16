#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORKFLOW="$REPO_ROOT/.github/workflows/delivery.yml"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/delivery/cases.json"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

assert_contains() {
  local text=$1 pattern=$2 message=$3
  grep -Eq -- "$pattern" <<<"$text" || fail "$message"
}

assert_absent() {
  local text=$1 pattern=$2 message=$3
  if grep -Eq -- "$pattern" <<<"$text"; then fail "$message"; fi
}

job_block() {
  local job=$1
  awk -v job="$job" '
    $0 == "  " job ":" { found = 1 }
    found && $0 ~ /^  [a-zA-Z0-9_-]+:$/ && $0 != "  " job ":" { exit }
    found { print }
  ' "$WORKFLOW"
}

step_script() {
  local name=$1
  awk -v name="$name" '
    $0 == "      - name: " name { found = 1; next }
    found && /^        run: \|$/ { body = 1; next }
    body && $0 !~ /^          / && $0 !~ /^$/ { exit }
    body { sub(/^          /, ""); print }
  ' "$WORKFLOW"
}

test_classification_and_skeleton() {
  [ -f "$WORKFLOW" ] || fail 'Missing ordered delivery workflow.'
  [ -f "$FIXTURES" ] || fail 'Missing delivery fixtures.'
  local workflow triggers permissions concurrency changes validation images classifier test_dir
  workflow="$(cat "$WORKFLOW")"
  triggers="$(sed -n '/^on:$/,/^permissions:$/p' "$WORKFLOW")"
  permissions="$(sed -n '/^permissions:$/,/^[^[:space:]]/p' "$WORKFLOW")"
  concurrency="$(sed -n '/^concurrency:$/,/^[^[:space:]]/p' "$WORKFLOW")"
  changes="$(job_block changes)"
  validation="$(job_block validation)"
  images="$(job_block publish-images)"

  assert_contains "$triggers" '^  push:$' 'Delivery must run on pushes.'
  assert_contains "$triggers" '^      - main$' 'Delivery push must target main.'
  assert_contains "$triggers" '^  workflow_dispatch:$' 'Delivery must support manual dispatch.'
  assert_contains "$permissions" '^  contents: read$' 'Delivery must default to contents: read.'
  assert_absent "$permissions" 'write' 'Delivery must not grant global write permission.'
  assert_contains "$concurrency" '^  group: main-delivery$' 'Delivery needs one stable concurrency group.'
  assert_contains "$concurrency" '^  cancel-in-progress: false$' 'State mutation must never be cancelled by a later run.'
  assert_contains "$validation" 'uses: \./\.github/workflows/_validation\.yml' 'Delivery must always call reusable validation.'
  assert_contains "$images" "if:.*needs\.changes\.outputs\.application == 'true'" 'Image publication must skip non-application changes.'
  assert_contains "$images" 'uses: \./\.github/workflows/_publish-images\.yml' 'Application changes must use verified image publication.'
  assert_contains "$images" '^      packages: write$' 'Image caller alone needs package write access.'
  for output in application infrastructure shared dev prod layers; do
    assert_contains "$changes" "^      $output:" "Changes job must expose $output."
  done

  classifier="$(step_script 'Classify changed delivery layers')"
  [ -n "$classifier" ] || fail 'Classification step script is missing.'
  test_dir="$(mktemp -d)"
  trap 'rm -rf -- "$test_dir"' RETURN
  mkdir -p "$test_dir/bin"
  cat >"$test_dir/bin/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
jq -j '.paths[] | ., "\u0000"' <<<"${DELIVERY_CASE:?}"
EOF
  chmod +x "$test_dir/bin/git"

  while IFS= read -r case_json; do
    local name event output_file expected actual key
    name="$(jq -r '.name' <<<"$case_json")"
    event="$(jq -r '.event' <<<"$case_json")"
    output_file="$test_dir/$name.out"
    : >"$output_file"
    PATH="$test_dir/bin:$PATH" DELIVERY_CASE="$case_json" EVENT_NAME="$event" \
      BEFORE_SHA=1111111111111111111111111111111111111111 \
      AFTER_SHA=2222222222222222222222222222222222222222 \
      GITHUB_OUTPUT="$output_file" bash -Eeuo pipefail -c "$classifier"
    for key in application infrastructure shared dev prod layers; do
      expected="$(jq -r --arg key "$key" '.expected[$key]' <<<"$case_json")"
      actual="$(sed -n "s/^$key=//p" "$output_file")"
      [ "$actual" = "$expected" ] || fail "$name $key: expected $expected, got $actual"
    done
  done < <(jq -c '.classification[]' "$FIXTURES")
}

test_shared_retained_gate() {
  local block
  block="$(job_block shared-retained)"
  [ -n "$block" ] || fail 'Missing protected shared retained apply.'
  assert_contains "$block" '^    needs: \[changes, validation\]$' 'Shared mutation must wait for classification and validation.'
  assert_contains "$block" "if:.*infrastructure == 'true'.*validation\.result == 'success'" 'Shared mutation must be validation-gated.'
  assert_contains "$block" '^    environment: shared$' 'Shared mutation must use protected shared environment.'
  assert_contains "$block" '^    timeout-minutes: [0-9]+$' 'Shared mutation needs an explicit timeout.'
  assert_contains "$block" '^      contents: read$' 'Shared mutation needs read-only repository access.'
  assert_contains "$block" '^      id-token: write$' 'Shared mutation needs OIDC only at job scope.'
  assert_absent "$block" 'packages: write|actions: write|pull-requests: write' 'Shared mutation permissions are too broad.'
  assert_contains "$block" 'uses: Azure/login@[0-9a-f]{40}' 'Shared mutation must use pinned Azure login.'
  assert_contains "$block" 'client-id:.*AZURE_CLIENT_ID_SHARED' 'Shared mutation must use only the shared identity.'
  assert_absent "$block" 'AZURE_CLIENT_ID_(DEV|PROD|PLAN)' 'Shared mutation must not receive another identity.'
  assert_contains "$block" 'uses: opentofu/setup-opentofu@[0-9a-f]{40}' 'Shared mutation must use pinned OpenTofu setup.'
  assert_contains "$block" 'output -json postgres_firewall_ranges' 'Shared guard must fail closed from prior exact state.'
  assert_contains "$block" 'reconcile-postgres-egress\.sh retained' 'Shared guard must use retained reconciliation.'
  assert_contains "$block" '--reviewed-ranges-file' 'Shared guard must use the prior reviewed map.'
  assert_contains "$block" 'tofu .* plan ' 'Shared guard must plan shared state.'
  assert_contains "$block" '-out=.*plan\.bin' 'Shared guard must create a saved plan.'
  assert_contains "$block" 'tofu .* apply ' 'Shared guard must apply shared state.'
  assert_contains "$block" 'plan\.bin' 'Shared guard must apply its saved plan in the same job.'
  assert_contains "$block" "jq -s '\\.\[0\] \\+ \\.\[1\]'" 'Generated retained map must replace the prior reviewed map.'
  assert_contains "$block" 'plan-result=failure' 'Shared guard must report plan failure safely.'
  assert_contains "$block" 'apply-result=failure' 'Shared guard must report apply failure safely.'
  assert_absent "$block" 'TOFU_STATE_(CONTAINER|KEY)_(DEV|PROD)|\.tofu/envs/(dev|prod)' 'Shared identity must not access environment state.'
  assert_absent "$block" 'continue-on-error|-lock=false' 'Shared mutation must fail closed and retain state locking.'
}

assert_environment_job() {
  local job=$1 layer=$2 client=$3 predecessor=$4 block upper
  upper="$(tr '[:lower:]' '[:upper:]' <<<"$layer")"
  block="$(job_block "$job")"
  [ -n "$block" ] || fail "Missing $layer infrastructure job."
  assert_contains "$block" "needs:.*shared-retained.*$predecessor|needs:.*$predecessor.*shared-retained" "$layer must follow retained shared state and its predecessor."
  assert_contains "$block" '^    environment: '"$layer"'$' "$layer must use its environment boundary."
  assert_contains "$block" '^    timeout-minutes: [0-9]+$' "$layer needs an explicit timeout."
  assert_contains "$block" '^      contents: read$' "$layer needs read-only contents permission."
  assert_contains "$block" '^      id-token: write$' "$layer needs scoped OIDC permission."
  assert_absent "$block" 'packages: write|actions: write|pull-requests: write' "$layer permissions are too broad."
  assert_contains "$block" "client-id:.*AZURE_CLIENT_ID_$client" "$layer must use its deploy identity."
  assert_absent "$block" 'AZURE_CLIENT_ID_(SHARED|PLAN)' "$layer must not receive shared or plan identity."
  if [ "$layer" = dev ]; then
    assert_absent "$block" 'AZURE_CLIENT_ID_PROD' 'Dev must not receive prod identity.'
  else
    assert_absent "$block" 'AZURE_CLIENT_ID_DEV' 'Prod must not receive dev identity.'
  fi
  assert_contains "$block" "TOFU_STATE_CONTAINER_$upper" "$layer must use only its backend container."
  assert_contains "$block" "TOFU_STATE_KEY_$upper" "$layer must use only its backend key."
  assert_contains "$block" "TOFU_PLAN_VARS_$upper" "$layer must use its reviewed plan inputs."
  assert_absent "$block" 'TOFU_STATE_(CONTAINER|KEY)_(SHARED'"$([ "$layer" = dev ] && printf '|PROD' || printf '|DEV')"')' "$layer must not access another state root."
  assert_contains "$block" "APPLY_LAYER:.*needs\.changes\.outputs\.$layer" "$layer apply must follow explicit path output."
  assert_contains "$block" 'output -json reviewed_postgres_egress' "$layer must collect its complete reviewed egress output."
  assert_contains "$block" 'uses: actions/upload-artifact@[0-9a-f]{40}' "$layer must transfer a sanitized output artifact."
  assert_contains "$block" 'retention-days: 1' "$layer handoff must expire after one day."
  assert_contains "$block" 'plan-result=failure' "$layer must report plan failure safely."
  assert_contains "$block" 'apply-result=failure' "$layer must report apply failure safely."
  assert_absent "$block" 'path:.*(plan|tfvars|\.log|tfstate)' "$layer must not upload plans, variables, logs, or state."
  assert_absent "$block" 'continue-on-error|-lock=false' "$layer must fail closed and retain state locking."
}

test_environment_order_approvals_and_handoff() {
  assert_environment_job dev-infrastructure dev DEV validation
  assert_environment_job prod-infrastructure prod PROD dev-infrastructure
  local prod
  prod="$(job_block prod-infrastructure)"
  assert_contains "$prod" 'needs:.*dev-infrastructure' 'Prod must wait for complete dev collection/application.'
  assert_contains "$prod" "needs\.dev-infrastructure\.result == 'success'" 'Prod must stop when dev fails.'
}

line_of() {
  local text=$1 pattern=$2
  grep -nE -- "$pattern" <<<"$text" | head -1 | cut -d: -f1
}

test_safe_egress_reconciliation() {
  local block retained_generate retained_apply exact_generate exact_apply verify stage
  block="$(job_block shared-reconcile)"
  [ -n "$block" ] || fail 'Missing final shared egress reconciliation job.'
  assert_contains "$block" 'needs:.*dev-infrastructure.*prod-infrastructure|needs:.*prod-infrastructure.*dev-infrastructure' 'Final reconciliation must wait for both environment handoffs.'
  assert_contains "$block" "needs\.prod-infrastructure\.result == 'success'" 'Final reconciliation must stop after environment failure.'
  assert_contains "$block" '^    environment: shared$' 'Final reconciliation requires shared approval.'
  assert_contains "$block" '^    timeout-minutes: [0-9]+$' 'Final reconciliation needs an explicit timeout.'
  assert_contains "$block" '^      contents: read$' 'Final reconciliation needs read-only contents.'
  assert_contains "$block" '^      id-token: write$' 'Final reconciliation needs shared OIDC.'
  assert_contains "$block" 'client-id:.*AZURE_CLIENT_ID_SHARED' 'Final reconciliation must use the shared identity.'
  assert_absent "$block" 'AZURE_CLIENT_ID_(DEV|PROD|PLAN)' 'Final reconciliation must not receive environment state identities.'
  assert_absent "$block" 'TOFU_STATE_(CONTAINER|KEY)_(DEV|PROD)|TOFU_PLAN_VARS_(DEV|PROD)|\.tofu/envs/(dev|prod)' 'Final shared job must not access environment state.'
  assert_contains "$block" 'uses: actions/download-artifact@[0-9a-f]{40}' 'Final reconciliation must download validated handoffs with a pinned action.'
  assert_contains "$block" 'postgres-egress-dev-' 'Final reconciliation must consume dev handoff.'
  assert_contains "$block" 'postgres-egress-prod-' 'Final reconciliation must consume prod handoff.'
  assert_contains "$block" '--dev-output-file' 'Final reconciliation must use pre-collected dev output.'
  assert_contains "$block" '--prod-output-file' 'Final reconciliation must use pre-collected prod output.'
  [ "$(grep -Ec -- "jq -s '\\.\[0\] \\+ \\.\[1\]'" <<<"$block" || true)" -eq 2 ] ||
    fail 'Retained and exact maps must each replace, not recursively merge, the prior reviewed map.'
  assert_absent "$block" "jq -s '\\.\[0\] \\* \\.\[1\]'" 'Recursive merge would retain obsolete firewall entries.'
  retained_generate="$(line_of "$block" 'reconcile-postgres-egress\.sh retained')"
  retained_apply="$(line_of "$block" 'retained-plan\.bin')"
  exact_generate="$(line_of "$block" 'reconcile-postgres-egress\.sh exact')"
  exact_apply="$(line_of "$block" 'exact-plan\.bin')"
  verify="$(line_of "$block" 'reconcile-postgres-egress\.sh verify')"
  [[ -n "$retained_generate" && -n "$retained_apply" && -n "$exact_generate" && -n "$exact_apply" && -n "$verify" ]] ||
    fail 'Final reconciliation must contain retained, exact, and verify phases.'
  ((retained_generate < retained_apply && retained_apply < exact_generate && exact_generate < exact_apply && exact_apply < verify)) ||
    fail 'Final reconciliation order must be retained generate/apply, exact generate/apply, then verify.'
  assert_absent "$block" 'continue-on-error|-lock=false' 'Egress transitions must fail closed and retain locking.'
  assert_contains "$block" '%s-plan-result=failure' 'Final reconciliation must label a failed saved plan.'
  assert_contains "$block" '%s-apply-result=failure' 'Final reconciliation must label a failed saved-plan apply.'
  for stage in dev-collection prod-collection retained-generation retained-plan retained-apply \
    exact-generation exact-plan exact-apply azure-list azure-broad-rule final-divergence; do
    jq -e --arg stage "$stage" '.egress_failures | index($stage) != null' "$FIXTURES" >/dev/null ||
      fail "Missing egress failure fixture: $stage"
  done
}

test_skip_combined_dependency_and_safe_summary() {
  local infrastructure application summary gate infrastructure_gate test_dir case_json expected actual
  infrastructure="$(job_block infrastructure-complete)"
  application="$(job_block application-ready)"
  summary="$(job_block delivery-summary)"
  [ -n "$infrastructure" ] || fail 'Missing explicit infrastructure completion gate.'
  [ -n "$application" ] || fail 'Missing future application readiness gate.'
  [ -n "$summary" ] || fail 'Missing secret-safe delivery summary.'
  assert_contains "$infrastructure" 'needs:.*shared-reconcile' 'Infrastructure completion must wait for exact reconciliation.'
  assert_contains "$infrastructure" 'if:.*always\(\)' 'Infrastructure gate must inspect skipped and failed jobs.'
  assert_contains "$infrastructure" "INFRASTRUCTURE:.*needs\.changes\.outputs\.infrastructure" 'Infrastructure gate must distinguish intentional skips.'
  assert_contains "$infrastructure" 'SHARED_RECONCILE_RESULT:.*needs\.shared-reconcile\.result' 'Infrastructure gate must reject reconciliation failure.'
  assert_contains "$application" 'needs:.*validation.*publish-images.*infrastructure-complete|needs:.*infrastructure-complete.*publish-images.*validation' 'Application readiness must wait for validation, images, and infrastructure.'
  assert_contains "$application" 'if:.*always\(\)' 'Application gate must inspect image skips and failures.'
  assert_contains "$application" 'WORKER_DIGEST:.*needs\.publish-images\.outputs\.worker-digest' 'Only application gate may pass verified worker digest onward.'
  assert_contains "$application" 'MIGRATE_DIGEST:.*needs\.publish-images\.outputs\.migrate-digest' 'Only application gate may pass verified migration digest onward.'
  assert_contains "$application" "IMAGES_RESULT.*== success|IMAGES_RESULT\" != success" 'Application changes must require successful images.'
  assert_contains "$application" "IMAGES_RESULT.*== skipped|IMAGES_RESULT\" != skipped" 'Non-application changes must require intentional image skip.'

  gate="$(step_script 'Evaluate application readiness')"
  [ -n "$gate" ] || fail 'Application readiness evaluator is missing.'
  test_dir="$(mktemp -d)"
  trap 'rm -rf -- "$test_dir"' RETURN
  while IFS= read -r case_json; do
    : >"$test_dir/gate.out"
    expected="$(jq -r '.ready' <<<"$case_json")"
    if APPLICATION="$(jq -r '.application' <<<"$case_json")" CHANGES_RESULT=success \
      VALIDATION_RESULT=success IMAGES_RESULT="$(jq -r '.images' <<<"$case_json")" \
      INFRASTRUCTURE_RESULT="$(jq -r '.infrastructure_result' <<<"$case_json")" \
      WORKER_DIGEST="$(printf 'a%.0s' {1..64})" MIGRATE_DIGEST="$(printf 'b%.0s' {1..64})" \
      GITHUB_OUTPUT="$test_dir/gate.out" bash -Eeuo pipefail -c "$gate" >/dev/null 2>&1; then
      actual="$(sed -n 's/^ready=//p' "$test_dir/gate.out")"
    else
      actual=false
    fi
    [ "$actual" = "$expected" ] || fail "$(jq -r '.name' <<<"$case_json") readiness mismatch"
  done < <(jq -c '.readiness[]' "$FIXTURES")

  infrastructure_gate="$(step_script 'Evaluate infrastructure completion')"
  [ -n "$infrastructure_gate" ] || fail 'Infrastructure completion evaluator is missing.'
  while IFS= read -r case_json; do
    : >"$test_dir/infrastructure-gate.out"
    expected="$(jq -r '.passes' <<<"$case_json")"
    if CHANGES_RESULT=success INFRASTRUCTURE="$(jq -r '.infrastructure' <<<"$case_json")" \
      SHARED_RETAINED_RESULT="$(jq -r '.results[0]' <<<"$case_json")" \
      DEV_RESULT="$(jq -r '.results[1]' <<<"$case_json")" \
      PROD_RESULT="$(jq -r '.results[2]' <<<"$case_json")" \
      SHARED_RECONCILE_RESULT="$(jq -r '.results[3]' <<<"$case_json")" \
      GITHUB_OUTPUT="$test_dir/infrastructure-gate.out" \
      bash -Eeuo pipefail -c "$infrastructure_gate" >/dev/null 2>&1; then
      actual=true
      [ "$(sed -n 's/^state=//p' "$test_dir/infrastructure-gate.out")" = "$(jq -r '.state' <<<"$case_json")" ] ||
        fail "$(jq -r '.name' <<<"$case_json") infrastructure state mismatch"
    else
      actual=false
    fi
    [ "$actual" = "$expected" ] || fail "$(jq -r '.name' <<<"$case_json") infrastructure gate mismatch"
  done < <(jq -c '.infrastructure_readiness[]' "$FIXTURES")

  assert_contains "$summary" 'COMMIT_SHA:.*github\.sha' 'Summary must report commit SHA.'
  assert_contains "$summary" 'CHANGED_LAYERS:.*needs\.changes\.outputs\.layers' 'Summary must report bounded changed layers.'
  assert_contains "$summary" 'retained.*count|RETAINED_COUNT' 'Summary must report retained egress count.'
  assert_contains "$summary" 'exact.*count|EXACT_COUNT' 'Summary must report exact egress count.'
  assert_contains "$summary" 'plan|PLAN' 'Summary must report plan results.'
  assert_contains "$summary" 'apply|APPLY' 'Summary must report apply results.'
  assert_contains "$summary" 'SHARED_JOB_RESULT:.*needs\.shared-retained\.result' 'Summary must include shared guard job status.'
  assert_contains "$summary" 'RECONCILE_JOB_RESULT:.*needs\.shared-reconcile\.result' 'Summary must include reconciliation job status.'
  assert_absent "$summary" 'secrets\.|vars\.|ARM_|AZURE_|TOFU_|tfvars|\.tfstate|resource.group|server.name|ranges|cat ' 'Summary must not expose resource or configuration values.'
}

test_global_permissions_pins_and_timeouts() {
  local workflow job block
  workflow="$(cat "$WORKFLOW")"
  if grep -E 'uses: [^[:space:]]+@' "$WORKFLOW" | grep -Ev '@[0-9a-f]{40}([[:space:]]|$)' >/dev/null; then
    fail 'Every external action in delivery must be pinned to a full SHA.'
  fi
  for job in changes shared-retained dev-infrastructure prod-infrastructure shared-reconcile infrastructure-complete application-ready delivery-summary; do
    block="$(job_block "$job")"
    assert_contains "$block" '^    timeout-minutes: [0-9]+$' "$job must set an explicit timeout."
  done
  assert_absent "$workflow" 'cancel-in-progress: true|continue-on-error:' 'Ordered mutation must not cancel or suppress failures.'
}

test_classification_and_skeleton
test_shared_retained_gate
test_environment_order_approvals_and_handoff
test_safe_egress_reconciliation
test_skip_combined_dependency_and_safe_summary
test_global_permissions_pins_and_timeouts
printf '%s\n' 'Delivery workflow contract checks passed.'

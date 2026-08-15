#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORKFLOW="$REPO_ROOT/.github/workflows/_validation.yml"

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

step_block() {
  local job="$1"
  local step="$2"
  job_block "$job" | awk -v step="$step" '
    $0 == "      - name: " step { found = 1 }
    found && $0 ~ /^      - name:/ && $0 != "      - name: " step { exit }
    found { print }
  '
}

assert_contains() {
  local text="$1"
  local pattern="$2"
  local message="$3"
  printf '%s\n' "$text" | grep -Eq -- "$pattern" || fail "$message"
}

assert_absent() {
  local text="$1"
  local pattern="$2"
  local message="$3"
  if printf '%s\n' "$text" | grep -Eq -- "$pattern"; then
    fail "$message"
  fi
}

assert_existing_jobs_preserved() {
  local job
  for job in format build unit-tests integration-tests buf-checks samples package-generation; do
    [ -n "$(job_block "$job")" ] || fail "Existing $job validation job was removed."
  done

  local top_level_permissions
  top_level_permissions="$(sed -n '/^permissions:$/,/^[^[:space:]]/p' "$WORKFLOW")"
  assert_contains "$top_level_permissions" '^  contents: read$' \
    'Validation must retain top-level contents: read permission.'
  assert_absent "$top_level_permissions" 'packages: write' \
    'Validation must not receive package write permission.'

  local integration_block
  integration_block="$(job_block integration-tests)"
  assert_contains "$integration_block" '^      DOTNET_ENVIRONMENT: Test$' \
    'Integration validation must identify its non-production Testcontainers environment.'
}

assert_image_job() {
  local job="$1"
  local image="$2"
  local dockerfile="$3"
  local contract_test="$4"
  local block
  block="$(job_block "$job")"

  [ -n "$block" ] || fail "Missing $job validation job."
  assert_contains "$block" 'uses: actions/checkout@[0-9a-f]{40}' \
    "$job must pin actions/checkout to a full commit SHA."
  assert_contains "$block" 'uses: docker/setup-qemu-action@[0-9a-f]{40}' \
    "$job must pin docker/setup-qemu-action to a full commit SHA."
  assert_contains "$block" 'uses: docker/setup-buildx-action@[0-9a-f]{40}' \
    "$job must pin docker/setup-buildx-action to a full commit SHA."
  assert_contains "$block" 'uses: docker/build-push-action@[0-9a-f]{40}' \
    "$job must pin docker/build-push-action to a full commit SHA."
  assert_contains "$block" "file: $dockerfile" \
    "$job must build the expected Dockerfile."
  assert_contains "$block" 'platforms: linux/amd64,linux/arm64' \
    "$job must contract-build amd64 and arm64."
  assert_contains "$block" 'push: false' \
    "$job must explicitly disable pushing."
  assert_contains "$block" "cache-from: type=gha,scope=$image" \
    "$job must read its isolated GHA cache."
  assert_contains "$block" "cache-to: type=gha,mode=max,scope=$image" \
    "$job must write its isolated GHA cache."
  assert_contains "$block" "run: bash .github/scripts/tests/$contract_test" \
    "$job must run the existing runtime image contract."
  assert_contains "$block" 'load: true' "$job must load a local image for scanning."
  assert_contains "$block" "tags: messagebridge-$image:validation" \
    "$job must give the scanned image a deterministic local tag."
  assert_absent "$block" 'push: true' "$job must never push during validation."
  assert_absent "$block" 'packages: write' "$job must not receive package write permission."
}

assert_trivy_policy() {
  local job="$1"
  local image="$2"
  local record block summary upload
  record="$(step_block "$job" "Record $image vulnerabilities")"
  block="$(step_block "$job" "Block fixable $image vulnerabilities")"
  summary="$(step_block "$job" "Summarize $image vulnerabilities")"
  upload="$(step_block "$job" "Upload $image vulnerability report")"

  [ -n "$record" ] || fail "Missing non-blocking $image vulnerability recording step."
  assert_contains "$record" 'uses: aquasecurity/trivy-action@[0-9a-f]{40}' \
    "$image recording scan must pin aquasecurity/trivy-action."
  assert_contains "$record" 'scan-type: image' "$image recording pass must scan an image."
  assert_contains "$record" "image-ref: messagebridge-$image:validation" \
    "$image recording pass must scan the local validation image."
  assert_contains "$record" 'severity: UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL' \
    "$image recording pass must retain findings at every severity."
  assert_contains "$record" 'ignore-unfixed: false' \
    "$image recording pass must include currently unfixed findings."
  assert_contains "$record" "exit-code: [\"']?0[\"']?" \
    "$image recording pass must not block validation."
  assert_contains "$record" "output: artifacts/trivy/$image.txt" \
    "$image recording pass must persist its report."

  [ -n "$block" ] || fail "Missing blocking $image vulnerability scan."
  assert_contains "$block" 'uses: aquasecurity/trivy-action@[0-9a-f]{40}' \
    "$image blocking scan must pin aquasecurity/trivy-action."
  assert_contains "$block" 'severity: HIGH,CRITICAL' \
    "$image blocking scan must select HIGH and CRITICAL findings."
  assert_contains "$block" 'ignore-unfixed: true' \
    "$image blocking scan must exempt currently unfixed findings."
  assert_contains "$block" "exit-code: [\"']?1[\"']?" \
    "$image blocking scan must fail on selected fixable findings."

  [ -n "$summary" ] || fail "Missing $image vulnerability summary step."
  assert_contains "$summary" 'if: always\(\)' "$image summary must run after a blocking result."
  assert_contains "$summary" 'GITHUB_STEP_SUMMARY' "$image findings must reach the step summary."
  assert_contains "$summary" "artifacts/trivy/$image.txt" \
    "$image summary must use the recorded report."

  [ -n "$upload" ] || fail "Missing $image vulnerability report upload."
  assert_contains "$upload" 'if: always\(\)' "$image report upload must survive scan failure."
  assert_contains "$upload" 'uses: actions/upload-artifact@[0-9a-f]{40}' \
    "$image report upload must pin actions/upload-artifact."
  assert_contains "$upload" "path: artifacts/trivy/$image.txt" \
    "$image report upload must use the recorded report."
}

assert_workflow_contract_job() {
  local block
  block="$(job_block workflow-contracts)"
  [ -n "$block" ] || fail 'Missing docker-free workflow-contracts validation job.'
  assert_contains "$block" 'uses: actions/checkout@[0-9a-f]{40}' \
    'Workflow contract job must pin actions/checkout.'
  local test
  for test in validation-image-jobs-contract publish-images-workflow-contract \
    verify-anonymous-image trivy-policy publish-packages-independence; do
    assert_contains "$block" "bash .github/scripts/tests/$test\\.test\\.sh" \
      "Workflow contract job must run $test.test.sh."
  done
  assert_absent "$block" 'docker/setup-|docker/build-|docker run|packages: write' \
    'Workflow contract job must stay docker-free and read-only.'
}

[ -f "$WORKFLOW" ] || fail "Missing validation workflow: $WORKFLOW"
assert_existing_jobs_preserved
assert_image_job worker-image worker src/MessageBridge.Worker/Dockerfile \
  worker-image-contract.test.sh
assert_image_job migration-image migrate src/MessageBridge.Worker/Dockerfile.migrate \
  migration-image-contract.test.sh
assert_trivy_policy worker-image worker
assert_trivy_policy migration-image migrate
assert_workflow_contract_job

printf '%s\n' 'Validation image jobs contract checks passed.'

#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORKFLOW="$REPO_ROOT/.github/workflows/_publish-images.yml"

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

assert_contains() {
  local text="$1" pattern="$2" message="$3"
  printf '%s\n' "$text" | grep -Eq -- "$pattern" || fail "$message"
}

assert_absent() {
  local text="$1" pattern="$2" message="$3"
  if printf '%s\n' "$text" | grep -Eq -- "$pattern"; then
    fail "$message"
  fi
}

assert_count() {
  local text="$1" pattern="$2" expected="$3" message="$4" actual
  actual="$(printf '%s\n' "$text" | grep -Ec -- "$pattern" || true)"
  [ "$actual" -eq "$expected" ] || fail "$message (expected $expected, got $actual)"
}

assert_publish_job() {
  local job="$1" image="$2" dockerfile="$3" repository="$4" block
  block="$(job_block "$job")"
  [ -n "$block" ] || fail "Missing $job."

  assert_contains "$block" '^    needs: scan-images$' "$job must wait for vulnerability scans."
  assert_contains "$block" '^      contents: read$' "$job must retain read-only contents access."
  assert_contains "$block" '^      packages: write$' "$job alone needs GHCR write access."
  assert_contains "$block" 'uses: docker/login-action@[0-9a-f]{40}' \
    "$job must use a digest-pinned GHCR login action."
  assert_contains "$block" 'registry: ghcr.io' "$job must authenticate only to GHCR."
  assert_contains "$block" 'uses: docker/setup-qemu-action@[0-9a-f]{40}' \
    "$job must pin QEMU setup."
  assert_contains "$block" 'uses: docker/setup-buildx-action@[0-9a-f]{40}' \
    "$job must pin Buildx setup."
  assert_contains "$block" 'uses: docker/build-push-action@[0-9a-f]{40}' \
    "$job must pin the image build action."
  assert_contains "$block" '^          id: build$|^        id: build$' \
    "$job build step must expose its action digest."
  assert_contains "$block" "file: $dockerfile" "$job must use $dockerfile."
  assert_contains "$block" "tags: $repository:" "$job must publish the exact public repository."
  assert_contains "$block" 'platforms: linux/amd64,linux/arm64' \
    "$job must publish amd64 and arm64."
  assert_contains "$block" 'push: true' "$job must push its verified image."
  assert_contains "$block" "cache-from: type=gha,scope=$image" "$job must read isolated GHA cache."
  assert_contains "$block" "cache-to: type=gha,mode=max,scope=$image" "$job must update isolated GHA cache."
  assert_contains "$block" 'sbom: true' "$job must publish an SBOM attestation."
  assert_contains "$block" 'provenance: mode=max' "$job must publish maximum provenance."
  assert_contains "$block" 'org\.opencontainers\.image\.source=' "$job must label OCI source."
  assert_contains "$block" 'org\.opencontainers\.image\.revision=' "$job must label OCI revision."
  assert_contains "$block" 'steps\.build\.outputs\.digest' "$job must consume build digest output."
  assert_contains "$block" 'docker buildx imagetools inspect' "$job must independently resolve tag to digest."
  assert_contains "$block" 'jq -er' "$job must parse Buildx inspection output with jq."
  assert_contains "$block" 'resolved_digest.*build_digest|build_digest.*resolved_digest' \
    "$job must cross-check resolved and build digests."
  assert_contains "$block" 'digest=\$\{build_digest#sha256:\}' \
    "$job output must be a bare digest accepted unchanged by dev/prod inputs."
  assert_contains "$block" '^    outputs:$' "$job must expose its verified digest."
}

[ -f "$WORKFLOW" ] || fail "Missing reusable image publication workflow: $WORKFLOW"
workflow="$(cat "$WORKFLOW")"
triggers="$(sed -n '/^on:$/,/^permissions:$/p' "$WORKFLOW")"
top_permissions="$(sed -n '/^permissions:$/,/^[^[:space:]]/p' "$WORKFLOW")"

assert_contains "$triggers" '^  workflow_call:$' 'Publication workflow must support workflow_call.'
assert_contains "$triggers" '^  workflow_dispatch:$' 'Publication workflow must support manual publication.'
assert_absent "$triggers" '^  push:' \
  'Publication workflow must not compete with the ordered main delivery workflow.'
for image in worker migrate; do
  assert_contains "$triggers" "^      $image-digest:" \
    "workflow_call must expose $image-digest."
  assert_contains "$triggers" "value: .*jobs\.verify\.outputs\.$image-digest" \
    "$image digest must be returned only by the anonymous verification gate."
done

assert_contains "$top_permissions" '^  contents: read$' \
  'Publication workflow must default to contents: read.'
assert_absent "$top_permissions" 'packages: write' \
  'Publication workflow must not grant package writes globally.'
assert_count "$workflow" '^      packages: write$' 2 \
  'Only the two image push jobs may receive packages: write.'

if grep -E 'uses: [^[:space:]]+@' "$WORKFLOW" | grep -Ev '@[0-9a-f]{40}([[:space:]]|$)' >/dev/null; then
  fail 'Every third-party action must be pinned to a full commit SHA.'
fi

scan_block="$(job_block scan-images)"
[ -n "$scan_block" ] || fail 'Missing pre-publication scan job.'
assert_contains "$scan_block" 'push: false' 'Pre-publication scan builds must not push.'
assert_contains "$scan_block" 'uses: aquasecurity/trivy-action@[0-9a-f]{40}' \
  'Pre-publication Trivy must be digest-pinned.'
assert_contains "$scan_block" 'severity: HIGH,CRITICAL' \
  'Pre-publication scan must select HIGH and CRITICAL findings.'
assert_contains "$scan_block" 'ignore-unfixed: true' \
  'Pre-publication blocking scan must ignore currently unfixed findings.'
assert_contains "$scan_block" "exit-code: [\"']?1[\"']?" \
  'Pre-publication scan must block fixable policy findings.'
assert_absent "$scan_block" 'packages: write' 'Scan job must not receive package write permission.'

assert_publish_job publish-worker worker src/MessageBridge.Worker/Dockerfile \
  ghcr.io/chanakya-net/whatsapp-messaging/worker
assert_publish_job publish-migrate migrate src/MessageBridge.Worker/Dockerfile.migrate \
  ghcr.io/chanakya-net/whatsapp-messaging/migrate

verify_block="$(job_block verify)"
[ -n "$verify_block" ] || fail 'Missing anonymous verification gate.'
assert_contains "$verify_block" 'needs: \[publish-worker, publish-migrate\]' \
  'Anonymous verification must wait for both image pushes.'
assert_count "$verify_block" 'verify-anonymous-image\.sh' 2 \
  'Verification gate must invoke the anonymous verifier for both images.'
assert_contains "$verify_block" 'ghcr\.io/chanakya-net/whatsapp-messaging/worker' \
  'Verification gate must anonymously pull the worker repository.'
assert_contains "$verify_block" 'ghcr\.io/chanakya-net/whatsapp-messaging/migrate' \
  'Verification gate must anonymously pull the migration repository.'
assert_contains "$verify_block" 'needs\.publish-worker\.outputs\.digest' \
  'Verification gate must use the exact worker digest output.'
assert_contains "$verify_block" 'needs\.publish-migrate\.outputs\.digest' \
  'Verification gate must use the exact migration digest output.'
assert_absent "$verify_block" 'login-action|docker login|packages: write|GITHUB_TOKEN|secrets\.' \
  'Anonymous verification must receive no registry credentials or package write permission.'

assert_absent "$workflow" 'Azure/login|az containerapp|NUGET_' \
  'Reusable image publication must not deploy Azure resources or couple NuGet publication.'

printf '%s\n' 'Reusable image publication workflow contract checks passed.'

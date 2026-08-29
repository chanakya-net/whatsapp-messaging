#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
PACKAGE_WORKFLOW="$REPO_ROOT/.github/workflows/publish-packages.yml"
IMAGE_WORKFLOWS=(
  "$REPO_ROOT/.github/workflows/_validation.yml"
  "$REPO_ROOT/.github/workflows/_publish-images.yml"
  "$REPO_ROOT/.github/workflows/delivery.yml"
)
EXPECTED_SHA256="1c41c4282e9fc285aae402ef38e60b8b03f45b2995a21d69d5e673c6f631bba2"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_contains() {
  local pattern="$1" message="$2"
  grep -Eq -- "$pattern" "$PACKAGE_WORKFLOW" || fail "$message"
}

file_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

[ -f "$PACKAGE_WORKFLOW" ] || fail 'NuGet publication workflow is missing.'
[ "$(file_sha256 "$PACKAGE_WORKFLOW")" = "$EXPECTED_SHA256" ] \
  || fail 'publish-packages.yml changed from the accepted independent baseline.'

triggers="$(sed -n '/^on:$/,/^permissions:$/p' "$PACKAGE_WORKFLOW")"
printf '%s\n' "$triggers" | grep -q '^  workflow_dispatch:$' \
  || fail 'NuGet manual publication trigger changed.'
printf '%s\n' "$triggers" | grep -q '^  push:$' \
  || fail 'NuGet tag publication trigger changed.'
printf '%s\n' "$triggers" | grep -q '^[[:space:]]*- "v\*"$' \
  || fail 'NuGet v* tag trigger changed.'
if printf '%s\n' "$triggers" | grep -q 'workflow_call:'; then
  fail 'NuGet publication must remain independent, not reusable by image delivery.'
fi

assert_contains '^  contents: read$' 'NuGet workflow must retain contents: read permission.'
assert_contains 'NUGET_SOURCE_URL:.*secrets\.NUGET_SOURCE_URL' \
  'NuGet source secret contract changed.'
assert_contains 'NUGET_API_KEY:.*secrets\.NUGET_API_KEY' \
  'NuGet API key secret contract changed.'
for project in MessageBridge.Contracts MessageBridge.Publisher MessageBridge.Publisher.EntityFrameworkCore; do
  assert_contains "dotnet pack src/$project/$project\\.csproj" \
    "NuGet workflow no longer packs $project."
done

if grep -Eiq 'ghcr\.io|docker/|Dockerfile|container image|packages: write|id-token: write' \
  "$PACKAGE_WORKFLOW"; then
  fail 'NuGet publication became coupled to container image publication.'
fi

for workflow in "${IMAGE_WORKFLOWS[@]}"; do
  [ -f "$workflow" ] || fail "Missing image workflow: $workflow"
  if grep -Eq 'NUGET_|dotnet nuget push|publish-packages\.yml' "$workflow"; then
    fail "Image workflow references independent NuGet publication: $workflow"
  fi
done

printf '%s\n' 'NuGet publication independence contract checks passed.'

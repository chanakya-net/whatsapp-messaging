#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
DOCKERFILE_PATH="$REPO_ROOT/src/MessageBridge.Worker/Dockerfile.migrate"
IMAGE_PREFIX="ghcr.io/chanakya-net/whatsapp-messaging/migrate-contract-test"
PLATFORMS=(linux/amd64 linux/arm64)

if [ ! -f "$DOCKERFILE_PATH" ]; then
  printf '%s\n' "Missing migration Dockerfile: $DOCKERFILE_PATH" >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  printf '%s\n' 'docker is required to run the migration image contract test.' >&2
  exit 1
fi

validate_dockerfile() {
  if ! grep -qE '^ARG DOTNET_SDK_IMAGE_DIGEST=[a-f0-9]{64}$' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must pin SDK base image with a digest in DOTNET_SDK_IMAGE_DIGEST.' >&2
    exit 1
  fi

  if ! grep -qE '^ARG DOTNET_ASPNET_IMAGE_DIGEST=[a-f0-9]{64}$' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must pin ASP.NET base image with a digest in DOTNET_ASPNET_IMAGE_DIGEST.' >&2
    exit 1
  fi

  if [ "$(grep -c 'FROM .*@sha256:' "$DOCKERFILE_PATH")" -lt 2 ]; then
    printf '%s\n' 'Dockerfile.migrate must reference digest-pinned base images.' >&2
    exit 1
  fi

  if ! grep -q '^ARG BUILDARCH$' "$DOCKERFILE_PATH" \
      || ! grep -q '^ARG TARGETARCH$' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must declare Buildx build and target architectures.' >&2
    exit 1
  fi

  if ! grep -q 'case "${BUILDARCH}"' "$DOCKERFILE_PATH" \
      || ! grep -q 'case "${TARGETARCH}"' "$DOCKERFILE_PATH" \
      || ! grep -q 'FROM --platform=\$BUILDPLATFORM .*sdk:10.0@sha256:' "$DOCKERFILE_PATH" \
      || ! grep -q 'FROM --platform=\$TARGETPLATFORM .*aspnet:10.0@sha256:' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must build natively and bundle for the target architecture.' >&2
    exit 1
  fi

  if ! grep -Eq 'dotnet.*tool.*dotnet-ef' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must build an EF Core migration bundle.' >&2
    exit 1
  fi
}

assert_non_root() {
  local image="$1"
  local user
  user="$(docker inspect "$image" --format '{{.Config.User}}')"
  if [ -z "$user" ] || [ "$user" = "root" ] || [ "$user" = "0" ]; then
    printf '%s\n' "Image $image runs as root." >&2
    exit 1
  fi
}

assert_target_architecture() {
  local image="$1"
  local platform="$2"
  local architecture

  architecture="$(docker inspect "$image" --format '{{.Architecture}}')"
  if [ "$architecture" != "${platform#linux/}" ]; then
    printf '%s\n' "Image $image has architecture $architecture, expected $platform." >&2
    exit 1
  fi
}

assert_entrypoint_and_absence() {
  local image="$1"
  local entrypoint cmdline
  entrypoint="$(docker inspect "$image" --format '{{json .Config.Entrypoint}}')"
  cmdline="$(docker inspect "$image" --format '{{json .Config.Cmd}}')"

  if ! echo "$entrypoint" | grep -q '"/app/migrate"'; then
    printf '%s\n' "Image $image must execute only the migration bundle." >&2
    exit 1
  fi

  if echo "$entrypoint $cmdline" | grep -q 'MessageBridge.Worker'; then
    printf '%s\n' "Image $image must not include worker command paths." >&2
    exit 1
  fi

  if docker inspect "$image" --format '{{range .Config.Env}}{{println .}}{{end}}' | \
      grep -Eq 'RabbitMq__|MESSAGEBRIDGE_CONNECTION_STRING|MESSAGEBRIDGE_ConnectionString|ConnectionStrings__'; then
    printf '%s\n' "Image $image contains disallowed worker/secret env metadata." >&2
    exit 1
  fi
}

assert_bundle_help() {
  local image="$1"
  local platform="$2"

  if ! docker run --rm --platform "$platform" "$image" --help >/tmp/migrate-help.txt 2>&1; then
    printf '%s\n' "Migration bundle help failed for $platform image $image." >&2
    printf '%s\n' "Output:" >&2
    cat /tmp/migrate-help.txt >&2 || true
    exit 1
  fi
}

build_and_check_platform() {
  local platform="$1"
  local tag="$2"

  printf '%s\n' "Building migration image for $platform as $tag..."
  docker buildx build \
    --platform "$platform" \
    --load \
    -f "$DOCKERFILE_PATH" \
    -t "$tag" \
    "$REPO_ROOT"

  assert_non_root "$tag"
  assert_target_architecture "$tag" "$platform"
  assert_entrypoint_and_absence "$tag"
  assert_bundle_help "$tag" "$platform"
}

cleanup() {
  docker rmi \
    "$IMAGE_PREFIX-linux-amd64-${RUN_ID}" \
    "$IMAGE_PREFIX-linux-arm64-${RUN_ID}" \
    >/dev/null 2>&1 || true
}

trap cleanup EXIT

RUN_ID="build-$(date +%s)-${RANDOM:-0}"
validate_dockerfile

for platform in "${PLATFORMS[@]}"; do
  safe_platform="${platform//\//-}"
  build_and_check_platform "$platform" "$IMAGE_PREFIX-$safe_platform-${RUN_ID}"
done

printf '%s\n' 'Migration image contract checks passed.'

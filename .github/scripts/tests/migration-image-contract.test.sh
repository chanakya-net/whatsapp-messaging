#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
DOCKERFILE_PATH="$REPO_ROOT/src/MessageBridge.Worker/Dockerfile.migrate"
IMAGE_PREFIX="ghcr.io/chanakya-net/whatsapp-messaging/migrate-contract-test"
HOST_ARCH="$(uname -m)"
DOTNET_SDK_AMD64_DIGEST=5657c5f725f2e8923f31b2eb9d743662f2e0be50a2bee41de685fc9f12ae68ef
DOTNET_SDK_ARM64_DIGEST=a62dc5f34a6f466228bda13acb9329b0abea86f837114dc2e34a7c48561b8dc6
DOTNET_ASPNET_AMD64_DIGEST=282c2e90dd35c6a720b744f4848d3dce9de4bfb404011270cc8ee63f07e56c36
DOTNET_ASPNET_ARM64_DIGEST=1971bacaf56d9a7c5cef1fac21fcffe8615d33586738fb4168d0d8a2a2f4e857

if [ "$HOST_ARCH" = "x86_64" ] || [ "$HOST_ARCH" = "amd64" ]; then
  PLATFORMS=(linux/amd64)
elif [ "$HOST_ARCH" = "aarch64" ] || [ "$HOST_ARCH" = "arm64" ]; then
  PLATFORMS=(linux/arm64)
else
  printf '%s\n' "Unsupported host architecture: $HOST_ARCH" >&2
  exit 1
fi

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

  if ! grep -q 'ARG TARGET_PLATFORM' "$DOCKERFILE_PATH"; then
    printf '%s\n' 'Dockerfile.migrate must include TARGET_PLATFORM for architecture-aware builds.' >&2
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
  local sdk_digest
  local aspnet_digest

  case "$platform" in
    linux/amd64)
      sdk_digest="$DOTNET_SDK_AMD64_DIGEST"
      aspnet_digest="$DOTNET_ASPNET_AMD64_DIGEST"
      ;;
    linux/arm64)
      sdk_digest="$DOTNET_SDK_ARM64_DIGEST"
      aspnet_digest="$DOTNET_ASPNET_ARM64_DIGEST"
      ;;
    *)
      printf '%s\n' "Unsupported target platform: $platform" >&2
      exit 1
      ;;
  esac

  printf '%s\n' "Building migration image for $platform as $tag..."
  docker buildx build \
    --platform "$platform" \
    --load \
    --build-arg DOTNET_SDK_IMAGE_DIGEST="$sdk_digest" \
    --build-arg DOTNET_ASPNET_IMAGE_DIGEST="$aspnet_digest" \
    --build-arg TARGET_PLATFORM="$platform" \
    -f "$DOCKERFILE_PATH" \
    -t "$tag" \
    "$REPO_ROOT"

  assert_non_root "$tag"
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

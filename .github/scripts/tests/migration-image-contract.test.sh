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

contains_disallowed_environment() {
  local environment="$1"
  local setting key

  while IFS= read -r setting; do
    [ -z "$setting" ] && continue
    key="${setting%%=*}"

    case "$key" in
      RabbitMq__*|MESSAGEBRIDGE_CONNECTION_STRING|MESSAGEBRIDGE_ConnectionString|ConnectionStrings__*)
        return 0
        ;;
    esac

    if printf '%s\n' "$key" | grep -Eiq '(password|secret|token)'; then
      return 0
    fi
  done <<< "$environment"

  return 1
}

validate_runtime_metadata() {
  local entrypoint="$1"
  local cmdline="$2"
  local environment="$3"

  if [ "$entrypoint" != '["/app/migrate"]' ]; then
    printf '%s\n' 'Entrypoint must equal ["/app/migrate"].' >&2
    return 1
  fi

  if [ "$cmdline" != 'null' ] && [ "$cmdline" != '[]' ]; then
    printf '%s\n' 'Cmd must be null or empty.' >&2
    return 1
  fi

  if contains_disallowed_environment "$environment"; then
    printf '%s\n' 'Environment contains worker or secret metadata.' >&2
    return 1
  fi
}

assert_runtime_metadata_contract_probes() {
  if ! declare -F validate_runtime_metadata >/dev/null; then
    printf '%s\n' 'Missing runtime metadata validator.' >&2
    exit 1
  fi

  if ! validate_runtime_metadata '["/app/migrate"]' '[]' 'DOTNET_RUNNING_IN_CONTAINER=true'; then
    printf '%s\n' 'Runtime metadata validation rejected the valid bundle-only contract.' >&2
    exit 1
  fi

  if validate_runtime_metadata '["/app/migrate","unexpected"]' 'null' '' 2>/dev/null; then
    printf '%s\n' 'Runtime metadata validation accepted an extra Entrypoint argument.' >&2
    exit 1
  fi

  if validate_runtime_metadata '["/app/migrate"]' '["unexpected"]' '' 2>/dev/null; then
    printf '%s\n' 'Runtime metadata validation accepted a non-empty Cmd.' >&2
    exit 1
  fi

  if validate_runtime_metadata '["/app/migrate"]' 'null' 'Database__Password=embedded-value' 2>/dev/null; then
    printf '%s\n' 'Runtime metadata validation accepted Database__Password.' >&2
    exit 1
  fi

  if validate_runtime_metadata '["/app/migrate"]' '[]' 'MigrationApiToken=embedded-value' 2>/dev/null; then
    printf '%s\n' 'Runtime metadata validation accepted a token-style environment key.' >&2
    exit 1
  fi

  if validate_runtime_metadata '["/app/migrate"]' 'null' 'ClientSecret=embedded-value' 2>/dev/null; then
    printf '%s\n' 'Runtime metadata validation accepted a secret-style environment key.' >&2
    exit 1
  fi
}

assert_entrypoint_and_absence() {
  local image="$1"
  local entrypoint cmdline environment
  entrypoint="$(docker inspect "$image" --format '{{json .Config.Entrypoint}}')"
  cmdline="$(docker inspect "$image" --format '{{json .Config.Cmd}}')"
  environment="$(docker inspect "$image" --format '{{range .Config.Env}}{{println .}}{{end}}')"

  if ! validate_runtime_metadata "$entrypoint" "$cmdline" "$environment"; then
    printf '%s\n' "Image $image has invalid runtime metadata." >&2
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
assert_runtime_metadata_contract_probes
validate_dockerfile

for platform in "${PLATFORMS[@]}"; do
  safe_platform="${platform//\//-}"
  build_and_check_platform "$platform" "$IMAGE_PREFIX-$safe_platform-${RUN_ID}"
done

printf '%s\n' 'Migration image contract checks passed.'

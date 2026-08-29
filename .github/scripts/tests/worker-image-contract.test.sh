#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
DOCKERFILE_PATH="$REPO_ROOT/src/MessageBridge.Worker/Dockerfile"
DOCKERIGNORE_PATH="$REPO_ROOT/.dockerignore"
DEPLOYMENT_DOC_PATH="$REPO_ROOT/docs/deployment.md"
IMAGE_PREFIX="ghcr.io/chanakya-net/whatsapp-messaging/worker-contract-test"
PLATFORMS=(linux/amd64 linux/arm64)

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

require_file() {
  [ -f "$1" ] || fail "Missing required file: $1"
}

require_pattern() {
  local pattern="$1"
  local path="$2"
  local message="$3"
  grep -Eq "$pattern" "$path" || fail "$message"
}

assert_immutable_deployment_reference() {
  local reference="$1"
  [[ "$reference" =~ ^ghcr\.io/chanakya-net/whatsapp-messaging/worker@sha256:[a-f0-9]{64}$ ]] || return 1
}

assert_non_root_user() {
  local user="$1"
  [ -n "$user" ] && [ "$user" != root ] && [ "$user" != 0 ]
}

assert_negative_contract_probes() {
  assert_immutable_deployment_reference \
    'ghcr.io/chanakya-net/whatsapp-messaging/worker@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' \
    || fail 'Immutable deployment reference probe was rejected.'
  if assert_immutable_deployment_reference 'ghcr.io/chanakya-net/whatsapp-messaging/worker:latest'; then
    fail 'Mutable deployment reference probe was accepted.'
  fi
  assert_non_root_user '$APP_UID' || fail 'APP_UID probe was rejected.'
  if assert_non_root_user root || assert_non_root_user 0 || assert_non_root_user ''; then
    fail 'Root execution probe was accepted.'
  fi
}

validate_dockerfile() {
  require_pattern '^ARG DOTNET_SDK_IMAGE_DIGEST=[a-f0-9]{64}$' "$DOCKERFILE_PATH" \
    'Worker SDK base-image digest must be pinned.'
  require_pattern '^ARG DOTNET_ASPNET_IMAGE_DIGEST=[a-f0-9]{64}$' "$DOCKERFILE_PATH" \
    'Worker ASP.NET base-image digest must be pinned.'
  require_pattern '^FROM --platform=\$BUILDPLATFORM mcr\.microsoft\.com/dotnet/sdk:10\.0@sha256:\$\{DOTNET_SDK_IMAGE_DIGEST\} AS build$' "$DOCKERFILE_PATH" \
    'Worker build stage must use the digest-pinned .NET 10 SDK image.'
  require_pattern '^FROM --platform=\$TARGETPLATFORM mcr\.microsoft\.com/dotnet/aspnet:10\.0@sha256:\$\{DOTNET_ASPNET_IMAGE_DIGEST\} AS runtime$' "$DOCKERFILE_PATH" \
    'Worker runtime stage must use the digest-pinned .NET 10 ASP.NET image.'
  require_pattern '^ARG BUILDARCH$' "$DOCKERFILE_PATH" \
    'Worker build stage must declare BUILDARCH.'
  require_pattern '^ARG TARGETARCH$' "$DOCKERFILE_PATH" \
    'Worker build stage must declare TARGETARCH.'
  require_pattern '^COPY .*\.csproj ' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must copy project files before restore.'
  require_pattern 'dotnet restore src/MessageBridge\.Worker/MessageBridge\.Worker\.csproj' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must restore the worker project.'
  require_pattern '^COPY src/ src/$' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must copy sources after restore.'
  require_pattern 'dotnet publish src/MessageBridge\.Worker/MessageBridge\.Worker\.csproj' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must publish the worker project.'
  require_pattern '^[[:space:]]*--configuration Release \\$' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must publish Release output.'
  require_pattern '^[[:space:]]*/p:UseAppHost=false$' "$DOCKERFILE_PATH" \
    'Worker Dockerfile must publish without an app host.'
  require_pattern '^ENV ASPNETCORE_HTTP_PORTS=8080$' "$DOCKERFILE_PATH" \
    'Worker must bind HTTP to port 8080.'
  require_pattern '^EXPOSE 8080$' "$DOCKERFILE_PATH" \
    'Worker image must expose port 8080.'
  require_pattern '^USER \$\{APP_UID\}$' "$DOCKERFILE_PATH" \
    'Worker image must execute as APP_UID.'
  require_pattern '^ENTRYPOINT \["dotnet", "MessageBridge\.Worker\.dll"\]$' "$DOCKERFILE_PATH" \
    'Worker entrypoint must start the worker assembly.'
}

validate_dockerignore() {
  local pattern
  for pattern in '^\.git/$' '^\*\*/bin/$' '^\*\*/obj/$' '^\*\*/TestResults/$' \
    '^\*\*/appsettings\*\.json$' '^\.env\.\*$' '^\*\.pem$' '^\*\.pfx$' \
    '^\*\.tfstate\.\*$' '^\*\.tfplan$' '^\*\*/\.terraform/$' '^\*\*/\.tofu/$'; do
    require_pattern "$pattern" "$DOCKERIGNORE_PATH" \
      ".dockerignore must exclude $pattern from the build context."
  done
}

validate_deployment_docs() {
  require_pattern 'ghcr\.io/chanakya-net/whatsapp-messaging/worker' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must identify the GHCR worker repository.'
  require_pattern 'immutable digest' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must require immutable digests.'
  require_pattern '/health/live' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must document the liveness probe.'
  require_pattern '/health/ready' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must document the readiness probe.'
  require_pattern 'linux/amd64' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must document linux/amd64 builds.'
  require_pattern 'linux/arm64' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must document linux/arm64 builds.'
  require_pattern 'non-root' "$DEPLOYMENT_DOC_PATH" \
    'Deployment guide must document the non-root invariant.'
  if grep -E '^[[:space:]]*image:[[:space:]]+[^[:space:]#]+:(latest|[A-Za-z0-9._-]+)[[:space:]]*$' "$DEPLOYMENT_DOC_PATH" \
      | grep -Fv '@sha256:' \
      | grep -Fv '${WORKER_IMAGE}'; then
    fail 'Deployment guide contains a mutable image reference.'
  fi
}

assert_image_metadata() {
  local image="$1"
  local platform="$2"
  local user architecture environment

  user="$(docker inspect "$image" --format '{{.Config.User}}')"
  assert_non_root_user "$user" || fail "Image $image runs as root."

  architecture="$(docker inspect "$image" --format '{{.Architecture}}')"
  [ "$architecture" = "${platform#linux/}" ] || fail "Image $image architecture is $architecture, expected $platform."

  environment="$(docker inspect "$image" --format '{{range .Config.Env}}{{println .}}{{end}}')"
  if printf '%s\n' "$environment" | grep -Eiq '^(.*(password|secret|token|connectionstring|connection_string).*)='; then
    fail "Image $image embeds sensitive environment metadata."
  fi
}

wait_for_liveness() {
  local port="$1"
  local attempt
  for attempt in $(seq 1 30); do
    if curl --fail --silent --show-error "http://127.0.0.1:$port/health/live" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  return 1
}

assert_health_endpoints() {
  local image="$1"
  local platform="$2"
  local container port ready_status

  container="$(docker run --detach --platform "$platform" -p 127.0.0.1::8080 \
    -e Database__Host=127.0.0.1 \
    -e Database__Database=contract \
    -e Database__Username=contract \
    -e Database__Password=contract-password \
    -e MessageBridge__ProcessingHistory__RecoveryEnabled=false \
    -e RabbitMq__ConnectionString='amqps://contract-user:contract-password@127.0.0.1:1/' \
    "$image")"
  port="$(docker port "$container" 8080/tcp | sed -n 's/.*:\([0-9][0-9]*\)$/\1/p')"
  if [ -z "$port" ]; then
    docker rm --force "$container" >/dev/null 2>&1 || true
    fail "Image $image did not publish port 8080."
  fi
  if ! wait_for_liveness "$port"; then
    docker logs "$container" >&2 || true
    docker rm --force "$container" >/dev/null 2>&1 || true
    fail "Image $image did not serve /health/live."
  fi
  ready_status="$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$port/health/ready")"
  docker rm --force "$container" >/dev/null 2>&1 || true
  [[ "$ready_status" =~ ^(200|503)$ ]] || fail "Image $image did not serve /health/ready."
}

build_and_check_platform() {
  local platform="$1"
  local tag="$2"
  docker buildx build --platform "$platform" --load -f "$DOCKERFILE_PATH" -t "$tag" "$REPO_ROOT"
  assert_image_metadata "$tag" "$platform"
  if [ "$platform" = "$HOST_PLATFORM" ]; then
    assert_health_endpoints "$tag" "$platform"
  fi
}

cleanup() {
  docker rmi "$@" >/dev/null 2>&1 || true
}

require_file "$DOCKERFILE_PATH"
require_file "$DOCKERIGNORE_PATH"
require_file "$DEPLOYMENT_DOC_PATH"
command -v docker >/dev/null 2>&1 || fail 'docker is required to run worker image contract tests.'
command -v curl >/dev/null 2>&1 || fail 'curl is required to run worker image contract tests.'
HOST_PLATFORM="$(docker version --format '{{.Server.Os}}/{{.Server.Arch}}')"

RUN_ID="build-$(date +%s)-${RANDOM:-0}"
TAGS=()
for platform in "${PLATFORMS[@]}"; do
  TAGS+=("$IMAGE_PREFIX-${platform//\//-}-$RUN_ID")
done
trap 'cleanup "${TAGS[@]}"' EXIT

assert_negative_contract_probes
validate_dockerfile
validate_dockerignore
validate_deployment_docs

for index in "${!PLATFORMS[@]}"; do
  build_and_check_platform "${PLATFORMS[$index]}" "${TAGS[$index]}"
done

printf '%s\n' 'Worker image contract checks passed.'

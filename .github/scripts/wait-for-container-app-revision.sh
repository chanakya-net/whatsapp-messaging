#!/usr/bin/env bash
# Poll Container Apps revision until expected digest is deployed and healthy.
# Returns 0 for healthy + digest match, 1 for unhealthy/mismatch, 124 for timeout.
set -euo pipefail

show_help() {
  cat <<'EOF'
Usage: wait-for-container-app-revision.sh [OPTIONS]
Poll a Container Apps revision until healthy with expected digest or timeout.

Options:
  --name NAME               App name (required)
  --resource-group GROUP    Resource group (required)
  --digest DIGEST           Expected image digest (required)
  --poll-budget N           Max poll attempts (default: 30)
  --timeout SECONDS         Max wall-clock seconds (default: 300)
  --help                    Show this help
EOF
}

parse_args() {
  APP_NAME=""
  RESOURCE_GROUP=""
  DIGEST=""
  POLL_BUDGET=30
  TIMEOUT=300

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name) APP_NAME="$2"; shift 2 ;;
      --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
      --digest) DIGEST="$2"; shift 2 ;;
      --poll-budget) POLL_BUDGET="$2"; shift 2 ;;
      --timeout) TIMEOUT="$2"; shift 2 ;;
      --help) show_help; exit 0 ;;
      *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
    esac
  done

  [ -n "$APP_NAME" ] || { printf 'Error: --name is required\n' >&2; exit 2; }
  [ -n "$RESOURCE_GROUP" ] || { printf 'Error: --resource-group is required\n' >&2; exit 2; }
  [ -n "$DIGEST" ] || { printf 'Error: --digest is required\n' >&2; exit 2; }
}

extract_image_digest() {
  local json=$1
  printf '%s\n' "$json" | jq -r '.properties.template.containers[0].image // ""' 2>/dev/null | grep -oE '@sha256:[a-f0-9]{64}|@sha256:[a-z0-9]+' || true
}

wait_for_revision() {
  local attempt=0 start_time status health image actual_digest

  start_time=$(date +%s)

  while [ "$attempt" -lt "$POLL_BUDGET" ]; do
    attempt=$((attempt + 1))

    # Check wall-clock timeout
    local now elapsed
    now=$(date +%s)
    elapsed=$((now - start_time))
    if [ "$elapsed" -ge "$TIMEOUT" ]; then
      printf 'Revision wait timed out after %d seconds.\n' "$elapsed" >&2
      return 124
    fi

    # Poll revision status
    local app_json
    app_json="$(az containerapp show --name "$APP_NAME" --resource-group "$RESOURCE_GROUP" \
      --output json 2>/dev/null)" || {
      printf 'Failed to query revision status.\n' >&2
      return 1
    }

    # Extract health and digest
    health="$(printf '%s\n' "$app_json" | jq -r '.properties.provisioning_state // "Unknown"' 2>/dev/null)"
    actual_digest="$(extract_image_digest "$app_json")"

    # Check digest match
    if [ -n "$actual_digest" ] && [[ "$actual_digest" != "@"* ]]; then
      actual_digest="@$actual_digest"
    fi

    if [ "$actual_digest" != "@$DIGEST" ] && [ "$actual_digest" != "$DIGEST" ]; then
      # Digest mismatch on deployed revision
      printf 'Revision digest mismatch: expected %s, got %s (health: %s).\n' "$DIGEST" "$actual_digest" "$health" >&2
      return 1
    fi

    # Digest matches - check health
    case "$health" in
      Healthy)
        printf 'Revision healthy with correct digest.\n'
        return 0
        ;;
      Unhealthy|Degraded)
        printf 'Revision %s with correct digest.\n' "$(printf '%s' "$health" | tr '[:upper:]' '[:lower:]')" >&2
        return 1
        ;;
    esac

    sleep 2
  done

  printf 'Revision wait timed out after %d polls.\n' "$POLL_BUDGET" >&2
  return 124
}

parse_args "$@"
wait_for_revision

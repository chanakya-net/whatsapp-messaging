#!/usr/bin/env bash
# Poll a named Container Apps revision until its expected digest is healthy.
set -euo pipefail

show_help() {
  cat <<'EOF'
Usage: wait-for-container-app-revision.sh [OPTIONS]
Poll a Container Apps revision until healthy with expected digest or timeout.

Options:
  --name NAME               App name (required)
  --revision NAME           Revision name (required)
  --resource-group GROUP    Resource group (required)
  --digest DIGEST           Expected image digest (required)
  --poll-budget N           Max poll attempts (default: 30)
  --timeout SECONDS         Max wall-clock seconds (default: 300)
  --help                    Show this help
EOF
}

parse_args() {
  APP_NAME="" REVISION_NAME="" RESOURCE_GROUP="" DIGEST="" POLL_BUDGET=30 TIMEOUT=300
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name) APP_NAME="$2"; shift 2 ;;
      --revision) REVISION_NAME="$2"; shift 2 ;;
      --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
      --digest) DIGEST="$2"; shift 2 ;;
      --poll-budget) POLL_BUDGET="$2"; shift 2 ;;
      --timeout) TIMEOUT="$2"; shift 2 ;;
      --help) show_help; exit 0 ;;
      *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
    esac
  done
  [ -n "$APP_NAME" ] || { printf 'Error: --name is required\n' >&2; exit 2; }
  [ -n "$REVISION_NAME" ] || { printf 'Error: --revision is required\n' >&2; exit 2; }
  [ -n "$RESOURCE_GROUP" ] || { printf 'Error: --resource-group is required\n' >&2; exit 2; }
  [ -n "$DIGEST" ] || { printf 'Error: --digest is required\n' >&2; exit 2; }
}

revision_context() {
  printf 'app %s revision %s' "$APP_NAME" "$REVISION_NAME"
}

timed_out() {
  [ "$(( $(date +%s) - START_TIME ))" -ge "$TIMEOUT" ]
}

sleep_until_next_poll() {
  local remaining delay=2
  remaining=$((TIMEOUT - ($(date +%s) - START_TIME)))
  [ "$remaining" -gt 0 ] || return 1
  [ "$remaining" -lt "$delay" ] && delay=$remaining
  sleep "$delay"
}

image_digest() {
  jq -r '.properties.template.containers[0].image // ""' <<<"$1" 2>/dev/null | grep -oE '@sha256:[a-z0-9]+' || true
}

terminal_failure() {
  case "$1" in Failed|Canceled|Cancelled|Deprovisioned|Unhealthy|Degraded) return 0 ;; esac
  return 1
}

wait_for_revision() {
  local attempt=0 revision_json provisioning health actual_digest
  START_TIME=$(date +%s)
  while [ "$attempt" -lt "$POLL_BUDGET" ]; do
    attempt=$((attempt + 1))
    if timed_out; then
      printf '%s timed out after %d seconds.\n' "$(revision_context)" "$TIMEOUT" >&2
      return 124
    fi
    revision_json="$(az containerapp revision show --name "$APP_NAME" --revision "$REVISION_NAME" \
      --resource-group "$RESOURCE_GROUP" --output json 2>/dev/null)" || {
      printf 'Failed to query %s.\n' "$(revision_context)" >&2
      return 1
    }
    provisioning="$(jq -r '.properties.provisioningState // "Unknown"' <<<"$revision_json" 2>/dev/null)"
    health="$(jq -r '.properties.healthState // "Unknown"' <<<"$revision_json" 2>/dev/null)"
    if terminal_failure "$provisioning" || terminal_failure "$health"; then
      printf '%s reached provisioning %s and health %s.\n' "$(revision_context)" "$provisioning" "$health" >&2
      return 1
    fi
    actual_digest="$(image_digest "$revision_json")"
    [ -z "$actual_digest" ] || actual_digest="${actual_digest#@}"
    if [ "$actual_digest" != "$DIGEST" ]; then
      printf '%s has provisioning %s, health %s, and digest %s (expected %s).\n' \
        "$(revision_context)" "$provisioning" "$health" "$actual_digest" "$DIGEST" >&2
      return 1
    fi
    if [ "$provisioning" = 'Provisioned' ] && [ "$health" = 'Healthy' ]; then
      printf '%s is Provisioned and Healthy with expected digest.\n' "$(revision_context)"
      return 0
    fi
    if [ "$attempt" -lt "$POLL_BUDGET" ] && ! sleep_until_next_poll; then
      printf '%s timed out after %d seconds.\n' "$(revision_context)" "$TIMEOUT" >&2
      return 124
    fi
  done
  printf '%s timed out after %d polls.\n' "$(revision_context)" "$POLL_BUDGET" >&2
  return 124
}

parse_args "$@"
wait_for_revision

#!/usr/bin/env bash
# Poll Container Apps Job execution until a terminal state or bounded timeout.
set -euo pipefail

show_help() {
  cat <<'EOF'
Usage: wait-for-container-app-job.sh [OPTIONS]
Poll a Container Apps Job execution until terminal state or timeout.

Options:
  --name NAME               Job name (required)
  --resource-group GROUP    Resource group (required)
  --poll-budget N           Max poll attempts (default: 30)
  --timeout SECONDS         Max wall-clock seconds (default: 300)
  --help                    Show this help
EOF
}

parse_args() {
  JOB_NAME="" RESOURCE_GROUP="" POLL_BUDGET=30 TIMEOUT=300
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name) JOB_NAME="$2"; shift 2 ;;
      --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
      --poll-budget) POLL_BUDGET="$2"; shift 2 ;;
      --timeout) TIMEOUT="$2"; shift 2 ;;
      --help) show_help; exit 0 ;;
      *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
    esac
  done
  [ -n "$JOB_NAME" ] || { printf 'Error: --name is required\n' >&2; exit 2; }
  [ -n "$RESOURCE_GROUP" ] || { printf 'Error: --resource-group is required\n' >&2; exit 2; }
}

job_context() {
  printf 'job %s execution %s' "$JOB_NAME" "$EXECUTION_NAME"
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

start_execution() {
  EXECUTION_NAME="$(az containerapp job start --name "$JOB_NAME" --resource-group "$RESOURCE_GROUP" \
    --output json 2>/dev/null | jq -r '.name // empty' 2>/dev/null)" || {
    printf 'Job %s failed to start an execution.\n' "$JOB_NAME" >&2
    return 1
  }
  [ -n "$EXECUTION_NAME" ] || {
    printf 'Job %s returned no execution identifier.\n' "$JOB_NAME" >&2
    return 1
  }
}

wait_for_job() {
  local attempt=0 status
  start_execution || return 1
  START_TIME=$(date +%s)

  while [ "$attempt" -lt "$POLL_BUDGET" ]; do
    attempt=$((attempt + 1))
    if timed_out; then
      printf '%s timed out after %d seconds.\n' "$(job_context)" "$TIMEOUT" >&2
      return 124
    fi
    status="$(az containerapp job execution show --name "$EXECUTION_NAME" --job "$JOB_NAME" \
      --resource-group "$RESOURCE_GROUP" --output json 2>/dev/null | jq -r '.properties.status // "Unknown"' 2>/dev/null)" || {
      printf 'Failed to query status for %s.\n' "$(job_context)" >&2
      return 1
    }
    case "$status" in
      Succeeded) printf '%s reached status Succeeded.\n' "$(job_context)"; return 0 ;;
      Failed|Degraded|Cancelled) printf '%s reached status %s.\n' "$(job_context)" "$status" >&2; return 1 ;;
    esac
    if [ "$attempt" -lt "$POLL_BUDGET" ] && ! sleep_until_next_poll; then
      printf '%s timed out after %d seconds.\n' "$(job_context)" "$TIMEOUT" >&2
      return 124
    fi
  done
  printf '%s timed out after %d polls.\n' "$(job_context)" "$POLL_BUDGET" >&2
  return 124
}

parse_args "$@"
wait_for_job

#!/usr/bin/env bash
# Poll Container Apps Job execution until terminal state (Succeeded, Failed, Degraded, Cancelled)
# or timeout. Returns 0 for Succeeded, 1 for terminal failures, 124 for timeout.
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
  JOB_NAME=""
  RESOURCE_GROUP=""
  POLL_BUDGET=30
  TIMEOUT=300

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

wait_for_job() {
  local attempt=0 start_time status execution

  # Start execution
  execution="$(az containerapp job start --name "$JOB_NAME" --resource-group "$RESOURCE_GROUP" \
    --output json 2>/dev/null | jq -r '.name' 2>/dev/null)" || {
    printf 'Failed to start job execution.\n' >&2
    return 1
  }

  start_time=$(date +%s)

  while [ "$attempt" -lt "$POLL_BUDGET" ]; do
    attempt=$((attempt + 1))

    # Check wall-clock timeout
    local now elapsed
    now=$(date +%s)
    elapsed=$((now - start_time))
    if [ "$elapsed" -ge "$TIMEOUT" ]; then
      printf 'Job execution timed out after %d seconds.\n' "$elapsed" >&2
      return 124
    fi

    # Poll execution status
    status="$(az containerapp job execution show --name "$execution" --job "$JOB_NAME" \
      --resource-group "$RESOURCE_GROUP" --output json 2>/dev/null | jq -r '.properties.status' 2>/dev/null)" || {
      printf 'Failed to query execution status.\n' >&2
      return 1
    }

    case "$status" in
      Succeeded)
        printf 'Execution Succeeded.\n'
        return 0
        ;;
      Failed|Degraded|Cancelled)
        printf 'Execution %s.\n' "$status"
        return 1
        ;;
    esac

    # Brief sleep between polls
    sleep 2
  done

  printf 'Job execution timed out after %d polls.\n' "$POLL_BUDGET" >&2
  return 124
}

parse_args "$@"
wait_for_job

#!/usr/bin/env bash
# Deterministic Azure CLI stand-in for Container Apps revision fixtures.
# Replays revision status and digest from FAKE_AZ_STREAM line by line.
# Stream format: "ProvisioningState:Digest" or "HealthStatus:Digest"
set -euo pipefail

printf 'az|%s\n' "$*" >>"$FAKE_AZ_CALL_LOG"

next_state() {
  local cursor total
  cursor="$(head -n 1 "$FAKE_AZ_STREAM_CURSOR" 2>/dev/null || true)"
  [ -n "$cursor" ] || cursor=0
  total="$(grep -c '' "$FAKE_AZ_STREAM" 2>/dev/null || echo 0)"

  # Bounded stream that runs out keeps reporting its last status.
  if [ "$cursor" -ge "$total" ] || [ "$total" -eq 0 ]; then
    if [ "$total" -gt 0 ]; then
      cursor=$((total - 1))
    else
      printf ''
      return
    fi
  fi

  head -n "$((cursor + 1))" "$FAKE_AZ_STREAM" 2>/dev/null | tail -n 1 2>/dev/null || true
  printf '%s\n' "$((cursor + 1))" >"$FAKE_AZ_STREAM_CURSOR"
}

case "$*" in
  'containerapp revision show '*)
    state="$(next_state)"

    # Parse "HealthState:sha256:digest" format, splitting on the first colon only
    if [[ "$state" == *:sha256:* ]]; then
      health="${state%%:sha256:*}"
      digest="sha256:${state#*:sha256:}"
    elif [[ "$state" == *:* ]]; then
      health="${state%:*}"
      digest="${state#*:}"
    else
      health="$state"
      digest=""
    fi

    # Default digest if not specified
    [ -n "$digest" ] || digest="sha256:abc123def456"

    # Build response JSON with revision healthState (camelCase per Azure API)
    printf '{"properties":{"healthState":"%s","template":{"containers":[{"image":"mcr.microsoft.com/app@%s"}]}}}\n' "$health" "$digest"
    ;;
  *)
    printf 'Unsupported az invocation in fixture: %s\n' "$*" >&2
    exit 97
    ;;
esac

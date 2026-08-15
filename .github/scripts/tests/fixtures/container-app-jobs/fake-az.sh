#!/usr/bin/env bash
# Deterministic Azure CLI stand-in for Container Apps Job execution fixtures.
#
# Reads one execution status per line from FAKE_AZ_STREAM and replays it through the same
# `az containerapp job execution show` shape the real CLI returns. Every invocation is appended to
# FAKE_AZ_CALL_LOG so a test can prove which commands an observer did and did not issue.
set -euo pipefail

printf 'az|%s\n' "$*" >>"$FAKE_AZ_CALL_LOG"

execution_name() {
  printf '%s\n' "${FAKE_AZ_EXECUTION_NAME:-mig-messagebridge-dev-cin-042-fixture}"
}

next_status() {
  local cursor total
  cursor="$(head -n 1 "$FAKE_AZ_STREAM_CURSOR" 2>/dev/null || true)"
  [ -n "$cursor" ] || cursor=0
  total="$(grep -c '' "$FAKE_AZ_STREAM")"

  # A bounded stream that runs out keeps reporting its last status, which is how a real
  # non-terminal execution behaves while an observer exhausts its poll budget.
  if [ "$cursor" -ge "$total" ]; then
    cursor=$((total - 1))
  fi

  head -n "$((cursor + 1))" "$FAKE_AZ_STREAM" | tail -n 1
  printf '%s\n' "$((cursor + 1))" >"$FAKE_AZ_STREAM_CURSOR"
}

case "$*" in
  'containerapp job start '*)
    printf '{"name":"%s","properties":{"status":"Running"}}\n' "$(execution_name)"
    ;;
  'containerapp job execution show '*)
    printf '{"name":"%s","properties":{"status":"%s"}}\n' "$(execution_name)" "$(next_status)"
    ;;
  'containerapp show '*)
    printf '{"properties":{"latestRevisionName":"%s"}}\n' "${FAKE_AZ_WORKER_REVISION:-ca-messagebridge-dev-cin-042--baseline}"
    ;;
  *)
    printf 'Unsupported az invocation in fixture: %s\n' "$*" >&2
    exit 97
    ;;
esac

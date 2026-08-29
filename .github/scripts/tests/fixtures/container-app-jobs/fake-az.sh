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
  cursor="$(cat "$FAKE_AZ_STREAM_CURSOR" 2>/dev/null || echo 0)" && cursor="${cursor%$'\n'}"
  [ -n "$cursor" ] || cursor=0

  # Count lines; empty file = 0 lines
  if [ ! -s "$FAKE_AZ_STREAM" ]; then
    total=0
  else
    total="$(wc -l < "$FAKE_AZ_STREAM")"
  fi

  # Empty stream means no execution; return error
  if [ "$total" -eq 0 ]; then
    return 1
  fi

  # A bounded stream that runs out keeps reporting its last status, which is how a real
  # non-terminal execution behaves while an observer exhausts its poll budget.
  if [ "$cursor" -ge "$total" ]; then
    cursor=$((total - 1))
  fi

  sed -n "$((cursor + 1))p" "$FAKE_AZ_STREAM"
  printf '%s\n' "$((cursor + 1))" >"$FAKE_AZ_STREAM_CURSOR"
}

case "$*" in
  'containerapp job start '*)
    printf '{"name":"%s","properties":{"status":"Running"}}\n' "$(execution_name)"
    ;;
  'containerapp job execution show '*)
    exec_status="$(next_status)" || {
      printf 'The job execution does not exist.\n' >&2
      exit 1
    }
    printf '{"name":"%s","properties":{"status":"%s"}}\n' "$(execution_name)" "$exec_status"
    ;;
  'containerapp show '*)
    printf '{"properties":{"latestRevisionName":"%s"}}\n' "${FAKE_AZ_WORKER_REVISION:-ca-messagebridge-dev-cin-042--baseline}"
    ;;
  *)
    printf 'Unsupported az invocation in fixture: %s\n' "$*" >&2
    exit 97
    ;;
esac

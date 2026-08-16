#!/usr/bin/env bash
# Deterministic Azure CLI stand-in for Container Apps revision fixtures.
set -euo pipefail

printf 'az|%s\n' "$*" >>"$FAKE_AZ_CALL_LOG"

require_revision_argument() {
  [[ " $* " == *" --name ${FAKE_AZ_APP_NAME} "* ]] || return 1
  [[ " $* " == *" --revision ${FAKE_AZ_REVISION_NAME} "* ]] || return 1
}

next_state() {
  local cursor total
  cursor="$(cat "$FAKE_AZ_STREAM_CURSOR" 2>/dev/null || echo 0)"; cursor="${cursor%$'\n'}"
  [ -n "$cursor" ] || cursor=0
  total="$(wc -l < "$FAKE_AZ_STREAM")"
  [ "$total" -gt 0 ] || return 1
  [ "$cursor" -lt "$total" ] || cursor=$((total - 1))
  sed -n "$((cursor + 1))p" "$FAKE_AZ_STREAM"
  printf '%s\n' "$((cursor + 1))" >"$FAKE_AZ_STREAM_CURSOR"
}

case "$*" in
  'containerapp revision show '*)
    require_revision_argument "$@" || {
      printf 'The revision argument is required.\n' >&2
      exit 2
    }
    state="$(next_state)" || {
      printf 'The revision does not exist.\n' >&2
      exit 1
    }
    [ "$state" != 'Missing' ] || {
      printf 'The revision does not exist.\n' >&2
      exit 1
    }
    IFS='|' read -r provisioning health digest <<<"$state"
    digest="${digest:-sha256:abc123def456}"
    printf '{"name":"%s","properties":{"provisioningState":"%s","healthState":"%s","template":{"containers":[{"image":"mcr.microsoft.com/app@%s"}]}}}\n' \
      "$FAKE_AZ_REVISION_NAME" "$provisioning" "$health" "$digest"
    ;;
  *)
    printf 'Unsupported az invocation in fixture: %s\n' "$*" >&2
    exit 97
    ;;
esac

#!/usr/bin/env bash
set -euo pipefail

printf 'tofu %s\n' "$*" >>"${FAKE_CALL_LOG:?}"
case "$*" in
  *" output -json reviewed_postgres_egress") ;;
  *) printf 'Unexpected tofu invocation: %s\n' "$*" >&2; exit 97 ;;
esac

root="${1#-chdir=}"
environment="$(basename "$root")"
jq -ce --arg scenario "${FAKE_SCENARIO:?}" --arg environment "$environment" \
  '(.base * .scenarios[$scenario])[$environment]' "${FAKE_CASES_FILE:?}"

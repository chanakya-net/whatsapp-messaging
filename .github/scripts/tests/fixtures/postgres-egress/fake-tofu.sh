#!/usr/bin/env bash
set -euo pipefail

printf 'tofu %s\n' "$*" >>"${FAKE_CALL_LOG:?}"
if [ "${FAKE_SCENARIO:?}" = tofu_output_failure ] && [[ "$*" == *"states/dev output -json reviewed_postgres_egress" ]]; then
  printf '%s\n' 'Injected OpenTofu output failure at secret-state-coordinate.' >&2
  exit 42
fi
case "$*" in
  *" output -json reviewed_postgres_egress") ;;
  *" output -json postgres_firewall_ranges") ;;
  *) printf 'Unexpected tofu invocation: %s\n' "$*" >&2; exit 97 ;;
esac

root="${1#-chdir=}"
environment="$(basename "$root")"
jq -ce --arg scenario "${FAKE_SCENARIO:?}" --arg environment "$environment" \
  '(.base * .scenarios[$scenario])[$environment]' "${FAKE_CASES_FILE:?}"

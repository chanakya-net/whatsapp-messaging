#!/usr/bin/env bash
set -euo pipefail

printf 'az %s\n' "$*" >>"${FAKE_CALL_LOG:?}"
if [ "${FAKE_SCENARIO:?}" = azure_list_failure ]; then
  printf '%s\n' 'Injected Azure list failure for 10.9.9.9 and secret-resource-coordinate.' >&2
  exit 42
fi
case "$*" in
  "postgres flexible-server firewall-rule list "*) ;;
  *) printf 'Forbidden Azure mutation or query: %s\n' "$*" >&2; exit 97 ;;
esac

jq -ce --arg scenario "${FAKE_SCENARIO:?}" \
  '(.base * .scenarios[$scenario]).azure' "${FAKE_CASES_FILE:?}"

#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >>"${FAKE_GH_LOG:?}"
[[ "${FAKE_GH_FAIL:-0}" != 1 ]] || exit 1

method=GET
endpoint=''
while (($#)); do
  case "$1" in
    --method)
      method="$2"
      shift 2
      ;;
    repos/*)
      endpoint="$1"
      shift
      ;;
    *) shift ;;
  esac
done

case "$method" in
  GET)
    jq -n --slurpfile page "${FAKE_GH_COMMENTS:?}" '$page'
    ;;
  POST|PATCH)
    request="$(cat)"
    jq -e '.body | type == "string"' <<<"$request" >/dev/null
    printf '%s\n' "$request" >>"${FAKE_GH_REQUESTS:?}"
    jq -n --arg method "$method" --arg endpoint "$endpoint" \
      '{id: 999, method: $method, endpoint: $endpoint}'
    ;;
  *) exit 1 ;;
esac

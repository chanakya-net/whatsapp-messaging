#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >>"${FAKE_CURL_LOG:?}"

url="${!#}"
output_file=""
header_file=""
authorization=""
arguments=("$@")

for ((index = 0; index < ${#arguments[@]}; index++)); do
  case "${arguments[$index]}" in
    --output)
      output_file="${arguments[$((index + 1))]}"
      ;;
    --dump-header)
      header_file="${arguments[$((index + 1))]}"
      ;;
    --header)
      if [[ "${arguments[$((index + 1))]}" == Authorization:* ]]; then
        authorization="${arguments[$((index + 1))]}"
      fi
      ;;
    -u | --user | --netrc | --config)
      printf '%s\n' 'credential-bearing curl option rejected by fixture' >&2
      exit 2
      ;;
  esac
done

if [[ "$url" == https://ghcr.io/token\?scope=repository:*:pull ]]; then
  printf '%s\n' '{"token":"fixture-token"}'
  exit 0
fi

if [[ "$url" != https://ghcr.io/v2/*/manifests/sha256:* ]]; then
  printf '%s\n' "unexpected fixture URL: $url" >&2
  exit 2
fi

[ "$authorization" = 'Authorization: Bearer fixture-token' ] || {
  printf '%s\n' 'missing anonymous bearer token' >&2
  exit 2
}

if [ "$FAKE_CURL_SCENARIO" = auth-required ]; then
  printf '%s\n' '{"errors":[{"code":"UNAUTHORIZED"}]}' >"$output_file"
  : >"$header_file"
  printf '401'
  exit 0
fi

fixture="$FAKE_CURL_FIXTURES/$FAKE_CURL_SCENARIO.json"
[ -f "$fixture" ] || {
  printf '%s\n' "missing manifest fixture: $fixture" >&2
  exit 2
}
cp "$fixture" "$output_file"
printf 'Docker-Content-Digest: sha256:%s\r\n' "$FAKE_EXPECTED_DIGEST" >"$header_file"
printf '200'

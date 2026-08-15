#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

if [ "$#" -ne 2 ]; then
  fail 'Usage: verify-anonymous-image.sh <repository> <64-character-sha256-digest>'
fi

repository="$1"
digest="$2"

case "$repository" in
  ghcr.io/chanakya-net/whatsapp-messaging/worker | \
    ghcr.io/chanakya-net/whatsapp-messaging/migrate) ;;
  *) fail "Repository is not approved for publication: $repository" ;;
esac

[[ "$digest" =~ ^[a-f0-9]{64}$ ]] \
  || fail 'Digest must be exactly 64 lowercase hexadecimal characters without a sha256: prefix.'
command -v curl >/dev/null 2>&1 || fail 'curl is required for anonymous image verification.'
command -v jq >/dev/null 2>&1 || fail 'jq is required for anonymous image verification.'

repository_path="${repository#ghcr.io/}"
token_url="https://ghcr.io/token?scope=repository:${repository_path}:pull"
manifest_url="https://ghcr.io/v2/${repository_path}/manifests/sha256:${digest}"

if ! token_response="$(curl --silent --show-error --fail --location "$token_url")"; then
  fail "Anonymous token request failed for $repository; ensure the GHCR package is public."
fi

if ! token="$(printf '%s' "$token_response" | jq -er \
  '(.token // .access_token) | select(type == "string" and length > 0)')"; then
  fail "GHCR did not return an anonymous pull token for $repository; ensure the package is public."
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT
manifest_file="$temp_dir/manifest.json"
header_file="$temp_dir/headers.txt"

if ! http_status="$(curl --silent --show-error --location \
  --output "$manifest_file" \
  --dump-header "$header_file" \
  --write-out '%{http_code}' \
  --header "Authorization: Bearer $token" \
  --header 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' \
  "$manifest_url")"; then
  fail "Anonymous manifest pull failed for $repository@sha256:$digest."
fi

if [ "$http_status" != 200 ]; then
  fail "Anonymous manifest pull returned HTTP $http_status; make the GHCR package public."
fi

returned_digest="$(awk '
  tolower($1) == "docker-content-digest:" { gsub("\\r", "", $2); print $2 }
' "$header_file" | tail -1)"
[ "$returned_digest" = "sha256:$digest" ] \
  || fail "Registry returned unexpected content digest: ${returned_digest:-missing}."

jq -e '.manifests | type == "array"' "$manifest_file" >/dev/null \
  || fail 'Published digest is not a multi-platform image index.'

for architecture in amd64 arm64; do
  jq -e --arg architecture "$architecture" '
    any(.manifests[]?; .platform.os == "linux" and .platform.architecture == $architecture)
  ' "$manifest_file" >/dev/null \
    || fail "Published image index is missing linux/$architecture."
done

printf 'Anonymous pull verified: %s@sha256:%s (linux/amd64, linux/arm64)\n' \
  "$repository" "$digest"

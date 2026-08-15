#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT="$REPO_ROOT/.github/scripts/verify-anonymous-image.sh"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/anonymous-image"
DIGEST="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
WORKER_REPOSITORY="ghcr.io/chanakya-net/whatsapp-messaging/worker"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

run_verifier() {
  local scenario="$1"
  shift
  export FAKE_CURL_SCENARIO="$scenario"
  : >"$FAKE_CURL_LOG"
  set +e
  "$SCRIPT" "$@" >"$TEST_DIR/stdout" 2>"$TEST_DIR/stderr"
  STATUS=$?
  set -e
}

[ -f "$SCRIPT" ] || fail "Missing anonymous image verifier: $SCRIPT"
[ -f "$FIXTURES/fake-curl.sh" ] || fail 'Missing fake curl fixture.'

TEST_DIR="$(mktemp -d)"
trap 'rm -rf -- "$TEST_DIR"' EXIT
mkdir -p "$TEST_DIR/bin"
cp "$FIXTURES/fake-curl.sh" "$TEST_DIR/bin/curl"
chmod +x "$TEST_DIR/bin/curl" "$SCRIPT"
export PATH="$TEST_DIR/bin:$PATH"
export FAKE_CURL_FIXTURES="$FIXTURES"
export FAKE_CURL_LOG="$TEST_DIR/curl.log"
export FAKE_EXPECTED_DIGEST="$DIGEST"

run_verifier success "$WORKER_REPOSITORY" "$DIGEST"
[ "$STATUS" -eq 0 ] || fail "Valid anonymous multi-arch image failed: $(cat "$TEST_DIR/stderr")"
grep -Fq "$WORKER_REPOSITORY@sha256:$DIGEST" "$TEST_DIR/stdout" \
  || fail 'Success output must identify the immutable image reference.'
grep -Fq 'https://ghcr.io/token?scope=repository:chanakya-net/whatsapp-messaging/worker:pull' \
  "$FAKE_CURL_LOG" || fail 'Verifier did not request an anonymous repository-scoped token.'
grep -Fq "https://ghcr.io/v2/chanakya-net/whatsapp-messaging/worker/manifests/sha256:$DIGEST" \
  "$FAKE_CURL_LOG" || fail 'Verifier did not fetch the manifest by immutable digest.'
if grep -Eq -- '(^|[[:space:]])(-u|--user|--netrc|--config)([[:space:]]|$)' "$FAKE_CURL_LOG"; then
  fail 'Verifier supplied registry credentials to curl.'
fi

run_verifier auth-required "$WORKER_REPOSITORY" "$DIGEST"
[ "$STATUS" -ne 0 ] || fail 'Private/auth-required package passed anonymous verification.'
grep -Eqi 'public|anonymous' "$TEST_DIR/stderr" \
  || fail 'Auth failure must explain that public anonymous access is required.'

run_verifier missing-arm64 "$WORKER_REPOSITORY" "$DIGEST"
[ "$STATUS" -ne 0 ] || fail 'Image index missing linux/arm64 passed verification.'
grep -Fq 'linux/arm64' "$TEST_DIR/stderr" \
  || fail 'Architecture failure must identify missing linux/arm64.'

run_verifier success "$WORKER_REPOSITORY" 'sha256:not-a-digest'
[ "$STATUS" -ne 0 ] || fail 'Malformed digest passed verification.'
[ ! -s "$FAKE_CURL_LOG" ] || fail 'Malformed digest must fail before any registry request.'

run_verifier success 'ghcr.io/chanakya-net/whatsapp-messaging/unapproved' "$DIGEST"
[ "$STATUS" -ne 0 ] || fail 'Repository outside the publication allowlist passed verification.'
[ ! -s "$FAKE_CURL_LOG" ] || fail 'Disallowed repository must fail before any registry request.'

printf '%s\n' 'Anonymous image verification fixture checks passed.'

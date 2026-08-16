#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
HELPER="$REPO_ROOT/.github/scripts/upsert-infra-plan-comment.sh"
FIXTURES="$REPO_ROOT/.github/scripts/tests/fixtures/infra-plan"
FIXTURE_DIR="$(mktemp -d)"
trap 'rm -rf "$FIXTURE_DIR"' EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_contains() {
  local file="$1" pattern="$2" message="$3"
  grep -Eq -- "$pattern" "$file" || fail "$message"
}

assert_absent() {
  local file="$1" pattern="$2" message="$3"
  if grep -Eiq -- "$pattern" "$file"; then
    fail "$message"
  fi
}

render_contract() {
  local summary="$FIXTURE_DIR/shared.md"
  bash "$HELPER" render shared "$FIXTURES/plan.json" "$summary"

  assert_contains "$summary" '^<!-- messagebridge-infra-plan:shared -->$' 'stable layer marker missing'
  assert_contains "$summary" 'Create: 1' 'create count missing'
  assert_contains "$summary" 'Update: 1' 'update count missing'
  assert_contains "$summary" 'Replace: 1' 'replacement count missing'
  assert_contains "$summary" 'azurerm_resource_group\.shared' 'code-visible metadata missing'
  assert_absent "$summary" 'hunter2|super-secret|before-value|after-value|output-secret|provider_config' \
    'state or secret-like plan values escaped sanitizer'
  assert_absent "$summary" 'dynamic-secret|malicious|script|\x1b' \
    'dynamic indices, Markdown, HTML, or control data escaped sanitizer'
  [[ "$(wc -c <"$summary" | tr -d ' ')" -le 60000 ]] || fail 'summary exceeded byte cap'

  if bash "$HELPER" render shared "$FIXTURES/malformed-plan.json" "$FIXTURE_DIR/malformed.md"; then
    fail 'malformed plan schema unexpectedly rendered'
  fi
  if bash "$HELPER" render bootstrap "$FIXTURES/plan.json" "$FIXTURE_DIR/bootstrap.md"; then
    fail 'unsupported bootstrap layer unexpectedly rendered'
  fi
}

bounded_contract() {
  local plan="$FIXTURE_DIR/oversized.json" summary="$FIXTURE_DIR/oversized.md"
  jq -n '{format_version:"1.2", resource_changes: [range(0; 2000) as $n | {
    address:("azurerm_resource.example[\"ignored-" + ($n|tostring) + "\"]"),
    mode:"managed", type:"azurerm_resource", name:("example_" + ($n|tostring)),
    change:{actions:["create"], before:null, after:{value:("x" * 1000)}}
  }]}' >"$plan"
  bash "$HELPER" render prod "$plan" "$summary"
  assert_contains "$summary" 'Showing first 100 of 2000 changed resources' \
    'oversized plan must report deterministic truncation'
  [[ "$(wc -c <"$summary" | tr -d ' ')" -le 60000 ]] || fail 'oversized summary exceeded cap'
  assert_absent "$summary" 'ignored-' 'dynamic resource indices must be omitted'
}

install_fake_gh() {
  mkdir -p "$FIXTURE_DIR/bin"
  cp "$FIXTURES/fake-gh.sh" "$FIXTURE_DIR/bin/gh"
  chmod +x "$FIXTURE_DIR/bin/gh"
  : >"$FIXTURE_DIR/gh.log"
  : >"$FIXTURE_DIR/requests.jsonl"
}

run_upsert() {
  local layer="$1" summary="$2" comments="$3"
  PATH="$FIXTURE_DIR/bin:$PATH" GH_TOKEN=fake-token \
    FAKE_GH_COMMENTS="$comments" FAKE_GH_LOG="$FIXTURE_DIR/gh.log" \
    FAKE_GH_REQUESTS="$FIXTURE_DIR/requests.jsonl" \
    bash "$HELPER" upsert "$layer" "$summary" chanakya-net/whatsapp-messaging 69
}

upsert_contract() {
  install_fake_gh
  local shared="$FIXTURE_DIR/shared.md" dev="$FIXTURE_DIR/dev.md"
  bash "$HELPER" render shared "$FIXTURES/plan.json" "$shared"
  bash "$HELPER" render dev "$FIXTURES/plan.json" "$dev"

  run_upsert dev "$dev" "$FIXTURES/existing-comments.json"
  assert_contains "$FIXTURE_DIR/gh.log" '--method POST repos/chanakya-net/whatsapp-messaging/issues/69/comments' \
    'missing marker must create one comment'

  : >"$FIXTURE_DIR/gh.log"
  run_upsert shared "$shared" "$FIXTURES/existing-comments.json"
  assert_contains "$FIXTURE_DIR/gh.log" '--method PATCH repos/chanakya-net/whatsapp-messaging/issues/comments/314' \
    'existing marker must update its comment'
  assert_absent "$FIXTURE_DIR/gh.log" '--method POST' 'existing marker must not create a duplicate'

  : >"$FIXTURE_DIR/gh.log"
  run_upsert shared "$shared" "$FIXTURES/foreign-author-comments.json"
  assert_contains "$FIXTURE_DIR/gh.log" '--method POST repos/chanakya-net/whatsapp-messaging/issues/69/comments' \
    'foreign marker must create a trusted bot comment'
  assert_absent "$FIXTURE_DIR/gh.log" '--method PATCH' 'foreign marker must never be adopted'

  if run_upsert shared "$shared" "$FIXTURES/duplicate-comments.json"; then
    fail 'duplicate layer markers unexpectedly accepted'
  fi
  if run_upsert shared "$shared" "$FIXTURES/duplicate-marker-comment.json"; then
    fail 'repeated marker in one comment unexpectedly accepted'
  fi
  if FAKE_GH_FAIL=1 run_upsert shared "$shared" "$FIXTURES/existing-comments.json"; then
    fail 'GitHub API failure unexpectedly accepted'
  fi

  cp "$shared" "$FIXTURE_DIR/bad-marker.md"
  printf '<!-- messagebridge-infra-plan:shared -->\n' >>"$FIXTURE_DIR/bad-marker.md"
  if run_upsert shared "$FIXTURE_DIR/bad-marker.md" "$FIXTURES/existing-comments.json"; then
    fail 'duplicate summary marker unexpectedly accepted'
  fi
  assert_absent "$FIXTURE_DIR/gh.log" 'fake-token' 'token must never be passed as an argument'
}

render_contract
bounded_contract
upsert_contract
printf 'PASS: infra plan comment sanitization, bounds, and upsert\n'

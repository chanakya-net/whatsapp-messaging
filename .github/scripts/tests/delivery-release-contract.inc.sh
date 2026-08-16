assert_release_results() {
  local case_json=$1 output_file=$2 exit_code=$3 name expected key actual
  name="$(jq -r '.name' <<<"$case_json")"
  expected="$(jq -r '.expected.exit' <<<"$case_json")"
  [[ "$exit_code" == "$expected" ]] || fail "$name exit: expected $expected, got $exit_code"
  for key in migration-result prior-digest revision-result smoke-result rollback-result; do
    expected="$(jq -r --arg key "$key" '.expected[$key]' <<<"$case_json")"
    actual="$(sed -n "s/^$key=//p" "$output_file" | tail -1)"
    [[ "$actual" == "$expected" ]] || fail "$name $key: expected $expected, got $actual"
  done
}

assert_happy_release_calls() {
  local call_log=$1 migration_update migration_start capture worker_update revision smoke_start
  migration_update="$(line_of "$(<"$call_log")" 'containerapp job update')"
  migration_start="$(line_of "$(<"$call_log")" 'containerapp job start.*mig-messagebridge')"
  capture="$(line_of "$(<"$call_log")" 'containerapp show.*containers.*image')"
  worker_update="$(line_of "$(<"$call_log")" '^containerapp update')"
  revision="$(line_of "$(<"$call_log")" 'containerapp revision show')"
  smoke_start="$(line_of "$(<"$call_log")" 'containerapp job start.*smoke-messagebridge')"
  ((migration_update < migration_start && migration_start < capture && capture < worker_update && \
    worker_update < revision && revision < smoke_start)) || fail 'Development release ordering is unsafe.'
  [[ "$(grep -c '^containerapp update' "$call_log")" == 1 ]] || fail 'Successful release must update worker once.'
  grep -Eq 'containerapp job update .*--image .*/migrate@sha256:b{64}' "$call_log" || fail 'Migration must use verified digest.'
  grep -Eq '^containerapp update .*--image .*/worker@sha256:a{64}' "$call_log" || fail 'Worker must use verified digest.'
}

assert_release_calls() {
  local case_json=$1 call_log=$2 name expected
  name="$(jq -r '.name' <<<"$case_json")"
  [[ "$(grep -c '^containerapp job update' "$call_log")" == 1 ]] || fail "$name must update migration image once."
  [[ "$(grep -c 'containerapp job start.*mig-messagebridge' "$call_log")" == 1 ]] || fail "$name must start migration once."
  if [[ "$name" == release-success ]]; then
    assert_happy_release_calls "$call_log"
  elif [[ "$name" == migration-* || "$name" == prior-digest-capture-failure ]]; then
    [[ "$(grep -c '^containerapp update' "$call_log" || true)" == 0 ]] || fail "$name must stop worker mutation."
  fi
  if jq -e '.expected["worker-updates"]' <<<"$case_json" >/dev/null; then
    expected="$(jq -r '.expected["worker-updates"]' <<<"$case_json")"
    [[ "$(grep -c '^containerapp update' "$call_log")" == "$expected" ]] || fail "$name worker update count mismatch."
    grep -Eq '^containerapp update .*--image .*@sha256:c{64}' "$call_log" || fail "$name must restore prior digest."
    expected="$(jq -r '.expected["smoke-starts"]' <<<"$case_json")"
    [[ "$(grep -c 'containerapp job start.*smoke-messagebridge' "$call_log" || true)" == "$expected" ]] || \
      fail "$name smoke verification count mismatch."
  fi
}

run_release_case() {
  local release_script=$1 test_dir=$2 case_json=$3 name output_file call_log state_dir exit_code=0
  name="$(jq -r '.name' <<<"$case_json")"
  output_file="$test_dir/$name.out"; call_log="$test_dir/$name.calls"; state_dir="$test_dir/$name-state"
  mkdir -p "$state_dir"; : >"$output_file"; : >"$call_log"
  PATH="$test_dir/bin:$PATH" FAKE_AZ_CASE="$case_json" FAKE_AZ_CALL_LOG="$call_log" \
    FAKE_AZ_STATE_DIR="$state_dir" GITHUB_OUTPUT="$output_file" RUNNER_TEMP="$test_dir" \
    IMAGE_ROOT=ghcr.io/chanakya-net/whatsapp-messaging \
    MIGRATION_JOB_NAME=mig-messagebridge-dev-cin-042 SMOKE_JOB_NAME=smoke-messagebridge-dev-cin-042 \
    WORKER_NAME=ca-messagebridge-dev-cin-042 RESOURCE_GROUP=rg-messagebridge-dev-centralindia-042 \
    WORKER_DIGEST="$(printf 'a%.0s' {1..64})" MIGRATE_DIGEST="$(printf 'b%.0s' {1..64})" \
    MIGRATION_POLL_BUDGET=2 MIGRATION_TIMEOUT=2 REVISION_POLL_BUDGET=2 REVISION_TIMEOUT=2 \
    SMOKE_POLL_BUDGET=2 SMOKE_TIMEOUT=2 bash -Eeuo pipefail -c "$release_script" >/dev/null 2>&1 || exit_code=$?
  assert_release_results "$case_json" "$output_file" "$exit_code"
  assert_release_calls "$case_json" "$call_log"
}

test_dev_release_scenarios() {
  local release_script fixture_az test_dir case_json
  release_script="$(step_script 'Migrate, deploy, smoke, and roll back development')"
  [ -n "$release_script" ] || fail 'Development release script is missing.'
  fixture_az="$REPO_ROOT/.github/scripts/tests/fixtures/delivery/dev-release/fake-az.sh"
  [ -f "$fixture_az" ] || fail 'Development release Azure fixture is missing.'
  test_dir="$(mktemp -d)"
  trap 'rm -rf -- "$test_dir"' RETURN
  mkdir -p "$test_dir/bin"
  install -m 700 "$fixture_az" "$test_dir/bin/az"
  while IFS= read -r case_json; do
    run_release_case "$release_script" "$test_dir" "$case_json"
  done < <(jq -c '.release[]' "$FIXTURES")
}

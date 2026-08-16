test_development_promotion_handoff() {
  local block handoff
  block="$(job_block dev-release)"
  handoff="$(step_script 'Create successful development promotion handoff')"
  [ -n "$handoff" ] || fail 'Development promotion handoff creator is missing.'
  assert_contains "$block" '^      worker-digest:.*steps\.handoff\.outputs\.worker-digest' \
    'Development release must expose only successfully tested worker digest.'
  assert_contains "$block" '^      migrate-digest:.*steps\.handoff\.outputs\.migrate-digest' \
    'Development release must expose only successfully tested migration digest.'
  assert_contains "$block" '^      handoff-artifact:.*steps\.handoff\.outputs\.artifact-name' \
    'Development release must expose the artifact name created by its successful attempt.'
  assert_contains "$block" 'uses: actions/upload-artifact@[0-9a-f]{40}' \
    'Development release must upload its promotion handoff with a pinned action.'
  assert_contains "$handoff" 'artifact-name=dev-promotion-%s-%s' \
    'Promotion handoff must record the artifact name created by its workflow attempt.'
  assert_contains "$handoff" 'HANDOFF_RUN_ID.*HANDOFF_RUN_ATTEMPT' \
    'Promotion artifact name must include its workflow run and producing attempt.'
  assert_contains "$block" 'name:.*steps\.handoff\.outputs\.artifact-name' \
    'Promotion upload must use the artifact name exposed by the handoff step.'
  assert_contains "$block" 'retention-days: 1' 'Promotion handoff must expire after one day.'
  assert_contains "$block" 'overwrite: false' 'Promotion handoff must forbid artifact replacement.'
  assert_contains "$handoff" 'environment.*dev' 'Promotion handoff must identify development.'
  assert_contains "$handoff" 'migration.*success' 'Promotion requires a successful development migration.'
  assert_contains "$handoff" 'revision.*success' 'Promotion requires a healthy development revision.'
  assert_contains "$handoff" 'smoke.*success' 'Promotion requires successful development smoke.'
  assert_contains "$handoff" 'rollback.*skipped' 'Promotion requires no development rollback.'
  assert_absent "$block" 'path:.*(log|tfstate|tfvars|plan)' \
    'Promotion artifact must not contain logs, state, plans, or configuration.'
}

assert_prod_documentation() {
  local docs
  docs="$(cat "$DEPLOYMENT_DOCS")"
  assert_contains "$docs" 'prod.*GitHub Environment' 'Deployment docs must require protected prod Environment configuration.'
  assert_contains "$docs" 'required reviewers' 'Deployment docs must require production reviewers.'
  assert_contains "$docs" 'approval.*before production OIDC|before production OIDC.*approval' \
    'Deployment docs must state approval precedes production OIDC.'
  assert_contains "$docs" 'never reverses database schema automatically' \
    'Deployment docs must preserve the application-only rollback boundary.'
}

assert_prod_gate_contract() {
  local block=$1 login_line validate_line
  assert_contains "$block" '^    needs: \[dev-release\]$' 'Production promotion must depend only on tested development release.'
  assert_contains "$block" "if:.*dev-release\.result == 'success'" 'Production promotion must require development success.'
  assert_contains "$block" '^    environment: prod$' 'Production promotion must use protected prod Environment.'
  assert_contains "$block" '^      group: production-promotion$' 'Production promotion needs stable environment concurrency.'
  assert_contains "$block" '^      cancel-in-progress: false$' 'Production promotion must not cancel active mutation.'
  assert_contains "$block" '^      contents: read$' 'Production promotion needs read-only contents.'
  assert_contains "$block" '^      id-token: write$' 'Production OIDC must remain job-scoped.'
  assert_absent "$block" 'packages: write|actions: write|pull-requests: write' 'Production permissions are too broad.'
  assert_contains "$block" 'uses: actions/download-artifact@[0-9a-f]{40}' 'Promotion must download immutable handoff with pinned action.'
  assert_contains "$block" 'name:.*needs\.dev-release\.outputs\.handoff-artifact' \
    'Promotion must consume the handoff from the development attempt that produced it.'
  assert_absent "$block" 'name: dev-promotion-.*github\.run_attempt' \
    'Promotion download must not derive its artifact from a later partial-rerun attempt.'
  assert_contains "$block" 'client-id:.*AZURE_CLIENT_ID_PROD' 'Production promotion must use production deployment identity.'
  assert_absent "$block" 'AZURE_CLIENT_ID_(DEV|SHARED|PLAN)' 'Production promotion must not receive another identity.'
  assert_contains "$block" 'TOFU_STATE_CONTAINER_PROD' 'Production promotion must use production state container.'
  assert_contains "$block" 'TOFU_STATE_KEY_PROD' 'Production promotion must use production state key.'
  assert_absent "$block" 'TOFU_STATE_(CONTAINER|KEY)_(DEV|SHARED)|TOFU_PLAN_VARS_' 'Promotion must not access another state root or plan inputs.'
  validate_line="$(line_of "$block" 'name: Validate exact development promotion handoff')"
  login_line="$(line_of "$block" 'name: Sign in with production deploy identity')"
  [[ -n "$validate_line" && -n "$login_line" && "$validate_line" -lt "$login_line" ]] ||
    fail 'Promotion handoff validation must precede production OIDC.'
  assert_contains "$block" 'WORKER_DIGEST:.*steps\.handoff\.outputs\.worker-digest' 'Production worker digest must come from validated handoff.'
  assert_contains "$block" 'MIGRATE_DIGEST:.*steps\.handoff\.outputs\.migrate-digest' 'Production migration digest must come from validated handoff.'
  assert_absent "$block" 'needs\.(publish-images|application-ready)|docker |buildx|build-push|az acr|oras |skopeo|crane|--tag|:latest' \
    'Production promotion must not rebuild, retag, copy, look up, or bypass tested digests.'
  assert_absent "$block" 'down migration|database rollback|schema rollback|migration job update.*prior' \
    'Production rollback must never reverse database schema.'
}

run_promotion_handoff_cases() {
  local validate=$1 test_dir case_json name manifest expected actual
  test_dir="$(mktemp -d)"
  trap 'rm -rf -- "$test_dir"' RETURN
  while IFS= read -r case_json; do
    name="$(jq -r '.name' <<<"$case_json")"
    manifest="$test_dir/$name.json"
    jq '.manifest' <<<"$case_json" >"$manifest"
    : >"$test_dir/$name.out"
    expected="$(jq -r '.passes' <<<"$case_json")"
    if HANDOFF_FILE="$manifest" EXPECTED_COMMIT="$(printf 'd%.0s' {1..40})" \
      EXPECTED_WORKER_DIGEST="$(printf 'a%.0s' {1..64})" EXPECTED_MIGRATE_DIGEST="$(printf 'b%.0s' {1..64})" \
      HANDOFF_RUN_ID=123 HANDOFF_RUN_ATTEMPT=4 GITHUB_OUTPUT="$test_dir/$name.out" \
      bash -Eeuo pipefail -c "$validate" >/dev/null 2>&1; then
      actual=true
    else
      actual=false
    fi
    [[ "$actual" == "$expected" ]] || fail "$name promotion handoff validation mismatch."
  done < <(jq -c '.promotion_handoff[]' "$FIXTURES")
  rm -rf -- "$test_dir"
  trap - RETURN
}

test_prod_promotion_gate() {
  local block validate
  block="$(job_block prod-release)"
  [ -n "$block" ] || fail 'Missing protected production promotion job.'
  assert_prod_gate_contract "$block"
  assert_prod_documentation
  validate="$(step_script 'Validate exact development promotion handoff')"
  [ -n "$validate" ] || fail 'Production handoff validator is missing.'
  run_promotion_handoff_cases "$validate"
}

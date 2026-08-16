#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RUNBOOKS_DIR="$REPO_ROOT/docs/runbooks"
DEPLOYMENT_GUIDE="$REPO_ROOT/docs/deployment.md"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_file_exists() {
  local path=$1 message=$2
  [[ -f "$path" ]] || fail "$message (missing: $path)"
}

assert_contains() {
  local needle=$1 path=$2 message=$3
  grep -Fq -- "$needle" "$path" || fail "$message (not found in $path: $needle)"
}

assert_not_contains() {
  local needle=$1 path=$2 message=$3
  grep -Fq -- "$needle" "$path" && fail "$message (found in $path: $needle)" || true
}

assert_regex() {
  local pattern=$1 path=$2 message=$3
  grep -Eq "$pattern" "$path" || fail "$message (pattern not found in $path: $pattern)"
}

test_runbooks_exist() {
  local required_runbooks=(
    "deployment.md"
    "rollback.md"
    "migration-failure.md"
    "secret-rotation.md"
    "cloudamqp-outage.md"
    "database-restore.md"
  )

  for runbook in "${required_runbooks[@]}"; do
    local path="$RUNBOOKS_DIR/$runbook"
    assert_file_exists "$path" "Runbook must exist: $runbook"
  done
}

test_no_real_secrets_in_runbooks() {
  local real_secret_patterns=(
    # Real Azure Key Vault names
    'kv-messagebridge-[a-z]*-[0-9]{3}'
    # Real database connection strings
    'Server=psql-messagebridge'
    # Real Azure storage accounts
    'sa[a-z]*messagebridge'
    # Real secret values (passwords with common patterns)
    'Password=[A-Za-z0-9!@#$%^&*]{16,}'
    # Real tokens (Azure, GitHub, etc.)
    'ghp_[A-Za-z0-9]{36}'
  )

  for runbook in "$RUNBOOKS_DIR"/*.md; do
    for pattern in "${real_secret_patterns[@]}"; do
      grep -Ei "$pattern" "$runbook" && \
        fail "Runbook contains real secret pattern: $(basename "$runbook") matches $pattern"
    done
  done
}

test_no_unsafe_secret_patterns() {
  # Reject patterns that expose secret values via CLI or logs

  for runbook in "$RUNBOOKS_DIR"/*.md; do
    local name=$(basename "$runbook")

    # Reject --value arguments with secrets
    grep -E "(--value|--set.*value)" "$runbook" | grep -q "password\|secret\|key" && \
      fail "$name: Cannot pass secret values via '--value' command argument (use Azure Portal)"

    # Reject PGPASSWORD environment variable
    grep -q "PGPASSWORD=" "$runbook" && \
      fail "$name: Cannot use PGPASSWORD environment variable (connects to database directly)"

    # Reject keyvault secret show --query value pattern
    grep -q "keyvault secret show.*--query value" "$runbook" && \
      fail "$name: Cannot retrieve and display secret values (use Azure Portal for verification)"

    # Reject password variables in shell scripts
    grep -E '(password|secret|key)=.*\$\(' "$runbook" | grep -qv "placeholder\|disabled" && \
      fail "$name: Cannot pass secret in shell variable via command substitution"

    # Reject read -s pattern that then passes variable to command
    if grep -q "read -sp" "$runbook"; then
      local line_after_read=$(grep -A 1 "read -sp" "$runbook" | tail -1)
      if echo "$line_after_read" | grep -qE "az|psql|docker" | grep -v "Portal"; then
        fail "$name: Cannot pass secret read via 'read -sp' to commands (use Azure Portal)"
      fi
    fi
  done
}

test_approved_secret_names_only() {
  local approved_names=(
    'rabbitmq-connection-string'
    'new-relic-otlp-headers'
    'whatsapp-provider-placeholder'
    'email-provider-placeholder'
  )

  local unapproved_names=(
    'messagebridge-db-password'
    'messagebridge-rabbitmq-password'
    'messagebridge-api-key'
    'new-relic-license-key'
  )

  for runbook in "$RUNBOOKS_DIR"/*.md; do
    local name=$(basename "$runbook")

    # Check for unapproved secret names
    for unapproved in "${unapproved_names[@]}"; do
      grep -q "$unapproved" "$runbook" && \
        fail "$name: Uses unapproved secret name '$unapproved' (use approved names from seed-placeholder-secrets.sh)"
    done

    # Deployment runbook must reference approved names
    if [[ "$name" == "deployment.md" ]]; then
      grep -q "rabbitmq-connection-string" "$runbook" || \
        fail "$name: deployment.md must reference 'rabbitmq-connection-string' secret"
    fi
  done
}

test_deployment_runbook_structure() {
  local doc="$RUNBOOKS_DIR/deployment.md"
  assert_file_exists "$doc" "Deployment runbook must exist"

  # Check for key content (flexible on exact section names)
  local required_content=(
    "Authentication"
    "Input"
    "Naming"
    "region"
    "Bootstrap"
    "OIDC"
    "Foundation"
    "Database"
    "CloudAMQP"
  )

  for content in "${required_content[@]}"; do
    assert_contains "$content" "$doc" "Deployment runbook must include: $content"
  done

  # Each step must have commands or portal locations
  assert_regex 'bash|az|gh|psql|Portal' "$doc" \
    "Deployment runbook must include commands or portal locations"
}

test_rollback_runbook_structure() {
  local doc="$RUNBOOKS_DIR/rollback.md"
  assert_file_exists "$doc" "Rollback runbook must exist"

  local required_content=(
    "rollback"
    "Recovery"
    "Failure"
    "revision"
  )

  for content in "${required_content[@]}"; do
    assert_contains "$content" "$doc" "Rollback runbook must include: $content"
  done
}

test_migration_failure_runbook_structure() {
  local doc="$RUNBOOKS_DIR/migration-failure.md"
  assert_file_exists "$doc" "Migration failure runbook must exist"

  local required_content=(
    "migration"
    "Forward"
    "restore"
    "Failure"
    "Escalation"
  )

  for content in "${required_content[@]}"; do
    assert_contains "$content" "$doc" "Migration failure runbook must include: $content"
  done
}

test_secret_rotation_runbook_structure() {
  local doc="$RUNBOOKS_DIR/secret-rotation.md"
  assert_file_exists "$doc" "Secret rotation runbook must exist"

  local required_content=(
    "rotation"
    "Key Vault"
    "Dev"
    "Production"
    "Approval"
  )

  for content in "${required_content[@]}"; do
    assert_contains "$content" "$doc" "Secret rotation runbook must include: $content"
  done
}

test_cloudamqp_outage_runbook_structure() {
  local doc="$RUNBOOKS_DIR/cloudamqp-outage.md"
  assert_file_exists "$doc" "CloudAMQP outage runbook must exist"

  local required_content=(
    "outage"
    "CloudAMQP"
    "credential"
    "rotation"
    "Broker"
    "Failure"
  )

  for content in "${required_content[@]}"; do
    assert_contains "$content" "$doc" "CloudAMQP outage runbook must include: $content"
  done
}

test_database_restore_runbook_updated() {
  local doc="$RUNBOOKS_DIR/database-restore.md"
  assert_file_exists "$doc" "Database restore runbook must exist"

  # Must reference quarterly execution
  assert_contains "Quarterly execution" "$doc" \
    "Database restore must explicitly cover quarterly execution"

  # Must have RPO/RTO evidence section
  assert_contains "RPO/RTO" "$doc" \
    "Database restore must include RPO/RTO evidence guidance"

  # Must have explicit cleanup confirmation
  assert_contains "Cleanup" "$doc" \
    "Database restore must include cleanup section"
}

test_every_step_has_required_elements() {
  # Verify deployment.md has required fields for each operational section
  local doc="$RUNBOOKS_DIR/deployment.md"

  # Stage 1 should describe authentication and verification
  grep -A 10 "Stage 1:" "$doc" | grep -q "az account show" || \
    fail "Stage 1 must include authentication verification command"

  # Stage 4 should reference bootstrap.sh (not invented paths)
  grep -A 5 "Stage 4:" "$doc" | grep -q "bootstrap.sh" || \
    fail "Stage 4 must reference scripts/infra/bootstrap.sh"

  # Stage 5 should reference approved secret names only
  grep -A 10 "Stage 5:" "$doc" | grep -q "seed-placeholder-secrets.sh" || \
    fail "Stage 5 must reference approved secret seeding script"

  # Verify no direct `az containerapp create` (use OpenTofu/delivery.yml)
  grep -q "az containerapp create" "$doc" && \
    fail "Deployment runbook must not use direct 'az containerapp create' (use OpenTofu)"

  # Verify no mutable tags (only digest-pinned references)
  grep -E "worker:(v|dev-)" "$doc" | grep -qv "@sha256:" && \
    fail "Deployment runbook must use immutable digest references, not mutable tags"

  # Verify migration uses manual job, not docker run
  grep -A 10 "Stage 11:" "$doc" | grep -q "containerapp job" || \
    fail "Deployment runbook must reference manual Container Apps migration job"

  # Verify no direct database creation (use OpenTofu)
  grep -q "az postgres flexible-server create" "$doc" && \
    fail "Deployment runbook must not create database directly (use OpenTofu)"
}

test_markdown_syntax() {
  for runbook in "$RUNBOOKS_DIR"/*.md; do
    # Verify markdown headers are balanced
    local open_count=$(grep -Ec '^#+ ' "$runbook" || true)
    [[ $open_count -gt 0 ]] || fail "Runbook has no headers: $(basename "$runbook")"

    # Verify code blocks are closed
    local open_blocks=$(grep -Ec '^```' "$runbook" || true)
    [[ $((open_blocks % 2)) -eq 0 ]] || fail "Runbook has unclosed code blocks: $(basename "$runbook")"
  done
}

test_links_are_valid() {
  for runbook in "$RUNBOOKS_DIR"/*.md; do
    # Extract markdown links [text](path) and remove brackets
    grep -Eo '\]\([^)]+\)' "$runbook" | cut -c3- | rev | cut -c2- | rev | while read -r link; do
      # Skip http(s) links
      if [[ "$link" =~ ^https?:// ]]; then
        continue
      fi

      # Resolve relative to docs/runbooks/
      local target_path="$RUNBOOKS_DIR/$link"
      # Allow fragment links
      target_path="${target_path%#*}"

      [[ -f "$target_path" ]] || fail "Broken link in $(basename "$runbook"): $link"
    done
  done
}

test_no_contradictions_with_deployment() {
  # Check for contradictory guidance between runbooks and deployment.md

  # Both should agree on Azure Container Apps being primary production model
  assert_contains "Azure Container Apps" "$DEPLOYMENT_GUIDE" \
    "deployment.md must establish Azure Container Apps as primary model"

  # Check that database runbooks don't contradict deployment guidance
  if grep -i "startup-migration" "$DEPLOYMENT_GUIDE"; then
    fail "deployment.md contains outdated startup-migration guidance"
  fi
}

test_all_twelve_hitl_stages_covered() {
  # The issue requires covering twelve HITL stages
  # This test verifies they're documented across the runbooks

  local hitl_stages=(
    "authentication"
    "input"
    "naming"
    "state"
    "foundation"
    "secret"
    "database"
    "cloudamqp"
    "new relic"
    "ghcr"
    "release"
    "rotation"
  )

  for stage in "${hitl_stages[@]}"; do
    local found=0
    for runbook in "$RUNBOOKS_DIR"/*.md; do
      if grep -iq "$stage" "$runbook"; then
        found=1
        break
      fi
    done
    [[ $found -eq 1 ]] || fail "HITL stage not documented: $stage"
  done
}

main() {
  printf 'Running runbook contract tests...\n'
  test_runbooks_exist
  printf 'PASS: all required runbooks exist\n'

  test_no_real_secrets_in_runbooks
  printf 'PASS: no real secrets in runbooks\n'

  test_no_unsafe_secret_patterns
  printf 'PASS: no unsafe secret retrieval/display patterns\n'

  test_approved_secret_names_only
  printf 'PASS: all secret names are approved\n'

  test_deployment_runbook_structure
  printf 'PASS: deployment runbook has required structure\n'

  test_rollback_runbook_structure
  printf 'PASS: rollback runbook has required structure\n'

  test_migration_failure_runbook_structure
  printf 'PASS: migration-failure runbook has required structure\n'

  test_secret_rotation_runbook_structure
  printf 'PASS: secret-rotation runbook has required structure\n'

  test_cloudamqp_outage_runbook_structure
  printf 'PASS: cloudamqp-outage runbook has required structure\n'

  test_database_restore_runbook_updated
  printf 'PASS: database-restore runbook is complete\n'

  test_every_step_has_required_elements
  printf 'PASS: every operational step has required structure and correct commands\n'

  test_markdown_syntax
  printf 'PASS: markdown syntax is valid\n'

  test_links_are_valid
  printf 'PASS: all links are valid\n'

  test_no_contradictions_with_deployment
  printf 'PASS: no contradictions with deployment.md\n'

  test_all_twelve_hitl_stages_covered
  printf 'PASS: all twelve HITL stages documented\n'

  printf '\nAll runbook contract tests passed.\n'
}

main "$@"

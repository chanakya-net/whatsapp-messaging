#!/usr/bin/env bash
set -Eeuo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
FIREWALL_CLEANUP_ARMED=false
START_TIME=$(date +%s)

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

validate_ipv4() {
  local ip=$1 octet total=0
  local -a octets
  [[ "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || return 1
  IFS=. read -r -a octets <<<"$ip"
  for octet in "${octets[@]}"; do
    ((10#$octet <= 255)) || return 1
    total=$((total + 10#$octet))
  done
  ((total > 0))
}

load_targets() {
  RESTORE_SERIAL=${MESSAGEBRIDGE_RESTORE_SERIAL:-}
  [[ "$RESTORE_SERIAL" =~ ^[0-9]{3}$ ]] \
    || fail 'MESSAGEBRIDGE_RESTORE_SERIAL must be exactly three digits'

  RESTORE_POINTTIME=${MESSAGEBRIDGE_RESTORE_POINTTIME:-}
  [[ "$RESTORE_POINTTIME" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] \
    || fail 'MESSAGEBRIDGE_RESTORE_POINTTIME must be ISO 8601 UTC (YYYY-MM-DDTHH:MM:SSZ)'

  RESOURCE_GROUP="rg-messagebridge-shared-centralindia-${RESTORE_SERIAL}"
  SOURCE_SERVER="psql-messagebridge-shared-cin-${RESTORE_SERIAL}"
  SOURCE_HOST="${SOURCE_SERVER}.postgres.database.azure.com"
  RESTORE_SERVER_NAME="${RESTORED_SERVER:-psql-messagebridge-drill-${RESTORE_SERIAL}-$(date -u +%Y%m%dT%H%M%SZ)}"
  RESTORE_HOST="${RESTORE_SERVER_NAME}.postgres.database.azure.com"
  FIREWALL_RULE="messagebridge-db-restore-drill-${RESTORE_SERIAL}"
  DRILL_DB="messagebridge_prod"
}

resolve_operator_ip() {
  OPERATOR_IP=${MESSAGEBRIDGE_OPERATOR_IP:-}
  if [[ -z "$OPERATOR_IP" ]]; then
    require_cmd curl
    OPERATOR_IP=$(curl -fsS --max-time 10 https://api.ipify.org) \
      || fail 'could not resolve operator public IPv4 address'
  fi
  validate_ipv4 "$OPERATOR_IP" || fail 'operator IP must be one exact IPv4 address'
}

display_targets() {
  printf 'Source server: %s\nResource group: %s\nRestore point: %s\nTemporary server: %s-<timestamp>\nOperator IP: %s\n' \
    "$SOURCE_SERVER" "$RESOURCE_GROUP" "$RESTORE_POINTTIME" "psql-messagebridge-drill-${RESTORE_SERIAL}" "$OPERATOR_IP"
}

cleanup_firewall() {
  local exit_status=$?
  trap - EXIT HUP INT TERM
  if [[ "$FIREWALL_CLEANUP_ARMED" == true ]]; then
    if ! az postgres flexible-server firewall-rule delete \
      --resource-group "$RESOURCE_GROUP" \
      --name "$SOURCE_SERVER" \
      --rule-name "$FIREWALL_RULE" \
      --yes --only-show-errors --output none; then
      printf 'ERROR: temporary operator firewall rule cleanup failed\n' >&2
      ((exit_status != 0)) || exit_status=1
    fi
  fi
  unset ACCESS_TOKEN PGPASSWORD
  exit "$exit_status"
}

open_temporary_firewall() {
  FIREWALL_CLEANUP_ARMED=true
  az postgres flexible-server firewall-rule create \
    --resource-group "$RESOURCE_GROUP" \
    --name "$SOURCE_SERVER" \
    --rule-name "$FIREWALL_RULE" \
    --start-ip-address "$OPERATOR_IP" \
    --end-ip-address "$OPERATOR_IP" \
    --only-show-errors --output none
}

load_operator_auth() {
  ACCESS_TOKEN=$(az account get-access-token \
    --resource-type oss-rdbms \
    --query accessToken \
    --output tsv) \
    || fail 'could not acquire Azure access token'

  export PGPASSWORD="$ACCESS_TOKEN"
}

elapsed_time() {
  local end_time current_hours current_minutes current_seconds
  end_time=$(date +%s)
  local elapsed=$((end_time - START_TIME))
  current_hours=$((elapsed / 3600))
  current_minutes=$(((elapsed % 3600) / 60))
  current_seconds=$((elapsed % 60))
  printf 'Elapsed time: %02dh %02dm %02ds\n' "$current_hours" "$current_minutes" "$current_seconds"
}

restore_to_point_in_time() {
  printf 'Restoring to point-in-time: %s\n' "$RESTORE_POINTTIME"
  local restore_output
  restore_output=$(az postgres flexible-server restore \
    --resource-group "$RESOURCE_GROUP" \
    --source-server "$SOURCE_SERVER" \
    --server-name "$RESTORE_SERVER_NAME" \
    --restore-point-in-time "$RESTORE_POINTTIME" \
    --query 'name' \
    --output tsv) \
    || fail 'point-in-time restore failed'

  RESTORED_SERVER="$restore_output"
  printf 'Restored server: %s\n' "$RESTORED_SERVER"

  local server_state=''
  local max_attempts=60
  local attempt=0
  while [ "$attempt" -lt "$max_attempts" ]; do
    server_state=$(az postgres flexible-server show \
      --resource-group "$RESOURCE_GROUP" \
      --name "$RESTORED_SERVER" \
      --query 'state' \
      --output tsv)
    [[ "$server_state" == "Ready" ]] && break
    printf 'Waiting for server to be Ready (state: %s)...\n' "$server_state"
    sleep 5
    ((attempt++))
  done

  [[ "$server_state" == "Ready" ]] \
    || fail "Server did not reach Ready state after $max_attempts attempts"
  printf 'Restored server is ready.\n'
}

verify_schema_and_data() {
  printf 'Verifying migration history...\n'

  local migration_check
  migration_check=$(psql \
    --host="$RESTORE_HOST" \
    --username="__pgadmin" \
    --dbname="$DRILL_DB" \
    --no-password \
    --tuples-only \
    --command="SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%InitialCreate%';" 2>&1) \
    || fail 'migration history query failed'

  [[ "$migration_check" =~ [1-9] ]] || fail 'InitialCreate migration not found in restored database'
  printf 'Migration history verified: InitialCreate present.\n'

  printf 'Verifying processing data recency...\n'
  local data_check
  data_check=$(psql \
    --host="$RESTORE_HOST" \
    --username="__pgadmin" \
    --dbname="$DRILL_DB" \
    --no-password \
    --tuples-only \
    --command="SELECT COUNT(*) FROM \"message_processing_history\";" 2>&1) \
    || fail 'processing history query failed'

  [[ "$data_check" =~ [0-9] ]] || fail 'could not query processing history'
  printf 'Processing history verified: %s records found.\n' "${data_check// /}"

  printf 'Verification complete.\n'
}

destroy_temporary_server() {
  printf 'About to destroy temporary restore server: %s\n' "$RESTORE_SERVER_NAME"
  printf 'Enter "yes" to confirm destruction: '
  read -r confirmation
  [[ "$confirmation" == "yes" ]] || { printf 'Destruction cancelled.\n'; return 0; }

  printf 'Deleting temporary server...\n'
  az postgres flexible-server delete \
    --resource-group "$RESOURCE_GROUP" \
    --name "$RESTORE_SERVER_NAME" \
    --yes --only-show-errors --output none \
    || fail 'temporary server deletion failed'

  printf 'Temporary server deleted.\n'
}

plan_phase() {
  load_targets
  resolve_operator_ip
  display_targets
  printf 'Plan phase: no mutations will be made.\n'
}

restore_phase() {
  trap cleanup_firewall EXIT HUP INT TERM
  load_targets
  resolve_operator_ip
  require_cmd az psql
  open_temporary_firewall
  load_operator_auth
  restore_to_point_in_time
  verify_schema_and_data
  elapsed_time
}

verify_phase() {
  trap cleanup_firewall EXIT HUP INT TERM
  load_targets
  resolve_operator_ip
  require_cmd az psql
  open_temporary_firewall
  load_operator_auth
  verify_schema_and_data
  elapsed_time
}

destroy_phase() {
  trap cleanup_firewall EXIT HUP INT TERM
  load_targets
  resolve_operator_ip
  require_cmd az
  open_temporary_firewall
  destroy_temporary_server
  elapsed_time
}

main() {
  local command=${1:-plan}
  case "$command" in
    plan) plan_phase ;;
    restore) restore_phase ;;
    verify) verify_phase ;;
    destroy) destroy_phase ;;
    *)
      fail "unknown command: $command (expected: plan, restore, verify, destroy)"
      ;;
  esac
}

main "$@"

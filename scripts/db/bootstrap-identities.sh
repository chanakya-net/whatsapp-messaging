#!/usr/bin/env bash
set -Eeuo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
BOOTSTRAP_SQL="$REPO_ROOT/scripts/db/bootstrap-identities.sql"
FIREWALL_CLEANUP_ARMED=false

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

load_targets() {
  BOOTSTRAP_SERIAL=${MESSAGEBRIDGE_BOOTSTRAP_SERIAL:-}
  [[ "$BOOTSTRAP_SERIAL" =~ ^[0-9]{3}$ ]] \
    || fail 'MESSAGEBRIDGE_BOOTSTRAP_SERIAL must be exactly three digits'

  RESOURCE_GROUP="rg-messagebridge-shared-centralindia-${BOOTSTRAP_SERIAL}"
  SERVER="psql-messagebridge-shared-cin-${BOOTSTRAP_SERIAL}"
  SERVER_HOST="${SERVER}.postgres.database.azure.com"
  FIREWALL_RULE="messagebridge-db-bootstrap-${BOOTSTRAP_SERIAL}"
  DATABASES=(messagebridge_dev messagebridge_prod)
  RUNTIME_ROLES=(
    "id-messagebridge-runtime-dev-cin-${BOOTSTRAP_SERIAL}"
    "id-messagebridge-runtime-prod-cin-${BOOTSTRAP_SERIAL}"
  )
  MIGRATOR_ROLES=(
    "id-messagebridge-migrator-dev-cin-${BOOTSTRAP_SERIAL}"
    "id-messagebridge-migrator-prod-cin-${BOOTSTRAP_SERIAL}"
  )
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

display_targets() {
  local index
  printf 'Resource group: %s\nServer: %s\nServer host: %s\nOperator IP: %s\n' \
    "$RESOURCE_GROUP" "$SERVER" "$SERVER_HOST" "$OPERATOR_IP"
  for index in "${!DATABASES[@]}"; do
    printf 'Database: %s\n  Runtime: %s\n  Migrator: %s\n' \
      "${DATABASES[$index]}" "${RUNTIME_ROLES[$index]}" "${MIGRATOR_ROLES[$index]}"
  done
}

cleanup_firewall() {
  local exit_status=$?
  trap - EXIT HUP INT TERM
  if [[ "$FIREWALL_CLEANUP_ARMED" == true ]]; then
    if ! az postgres flexible-server firewall-rule delete \
      --resource-group "$RESOURCE_GROUP" \
      --name "$SERVER" \
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
    --name "$SERVER" \
    --rule-name "$FIREWALL_RULE" \
    --start-ip-address "$OPERATOR_IP" \
    --end-ip-address "$OPERATOR_IP" \
    --only-show-errors --output none
}

load_operator_auth() {
  ACCESS_TOKEN=$(az account get-access-token \
    --resource-type oss-rdbms \
    --query accessToken \
    --output tsv) || fail 'could not acquire PostgreSQL Entra access token'
  [[ -n "$ACCESS_TOKEN" && "$ACCESS_TOKEN" != *$'\n'* ]] \
    || fail 'Azure CLI returned an invalid PostgreSQL access token'

  OPERATOR_PRINCIPAL=${MESSAGEBRIDGE_OPERATOR_PRINCIPAL:-}
  if [[ -z "$OPERATOR_PRINCIPAL" ]]; then
    OPERATOR_PRINCIPAL=$(az account show --query user.name --output tsv) \
      || fail 'could not resolve the Entra operator principal'
  fi
  [[ -n "$OPERATOR_PRINCIPAL" && "$OPERATOR_PRINCIPAL" != *$'\n'* ]] \
    || fail 'Entra operator principal must be one non-empty value'
  PGPASSWORD=$ACCESS_TOKEN
  export PGPASSWORD
}

run_bootstrap_sql() {
  local database=$1 runtime_role=$2 migrator_role=$3 mode=$4 principals_only=${5:-off}
  local pgoptions
  pgoptions="-c messagebridge.runtime_role=$runtime_role"
  pgoptions+=" -c messagebridge.migrator_role=$migrator_role"
  pgoptions+=" -c messagebridge.bootstrap_mode=$mode"
  pgoptions+=" -c messagebridge.principals_only=$principals_only"
  PGOPTIONS="$pgoptions" PGSSLMODE=require PGCONNECT_TIMEOUT=15 psql \
    --host "$SERVER_HOST" \
    --port 5432 \
    --username "$OPERATOR_PRINCIPAL" \
    --dbname "$database" \
    --no-password \
    --no-psqlrc \
    --set=ON_ERROR_STOP=1 \
    --single-transaction \
    --file "$BOOTSTRAP_SQL"
}

run_principal_bootstrap() {
  local index
  for index in "${!DATABASES[@]}"; do
    run_bootstrap_sql postgres \
      "${RUNTIME_ROLES[$index]}" "${MIGRATOR_ROLES[$index]}" apply on
  done
}

run_environment_mode() {
  local mode=$1 index
  for index in "${!DATABASES[@]}"; do
    run_bootstrap_sql "${DATABASES[$index]}" \
      "${RUNTIME_ROLES[$index]}" "${MIGRATOR_ROLES[$index]}" "$mode"
  done
}

run_protected() {
  local command_name=$1
  require_cmd az
  require_cmd psql
  [[ -r "$BOOTSTRAP_SQL" ]] || fail "bootstrap SQL not found: $BOOTSTRAP_SQL"
  trap cleanup_firewall EXIT
  trap 'exit 129' HUP
  trap 'exit 130' INT
  trap 'exit 143' TERM
  open_temporary_firewall
  load_operator_auth
  if [[ "$command_name" == apply ]]; then
    run_principal_bootstrap
    run_environment_mode apply
  fi
  run_environment_mode verify
}

usage() {
  printf 'Usage: %s {plan|apply|verify}\n' "${0##*/}" >&2
  exit 2
}

main() {
  local command_name=${1:-}
  case "$command_name" in
    plan|apply|verify) ;;
    *) usage ;;
  esac
  load_targets
  resolve_operator_ip
  display_targets
  [[ "$command_name" == plan ]] || run_protected "$command_name"
}

main "$@"

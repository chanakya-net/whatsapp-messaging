#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
MODULE_DIR="$REPO_ROOT/.tofu/modules/database"
SHARED_DIR="$REPO_ROOT/.tofu/envs/shared"
TF_FILES=("$MODULE_DIR"/*.tf "$SHARED_DIR"/*.tf)
SERVER_BLOCK="$(sed -n '/^resource "azurerm_postgresql_flexible_server" "this"/,/^}/p' "$MODULE_DIR/main.tf")"

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

assert_present() {
  local pattern="$1"
  local path="$2"
  local message="$3"
  grep -Eq "$pattern" "$path" || fail "$message"
}

assert_absent() {
  local pattern="$1"
  local message="$2"
  shift 2
  if grep -En "$pattern" "$@"; then
    fail "$message"
  fi
}

assert_present 'version[[:space:]]*=[[:space:]]*"17"' "$MODULE_DIR/main.tf" 'PostgreSQL 17 is required.'
assert_present 'sku_name[[:space:]]*=[[:space:]]*"B_Standard_B1ms"' "$MODULE_DIR/main.tf" 'B1ms is required.'
assert_present 'storage_mb[[:space:]]*=[[:space:]]*32768' "$MODULE_DIR/main.tf" '32 GiB storage is required.'
assert_present 'backup_retention_days[[:space:]]*=[[:space:]]*14' "$MODULE_DIR/main.tf" '14-day backups are required.'
assert_present 'geo_redundant_backup_enabled[[:space:]]*=[[:space:]]*false' "$MODULE_DIR/main.tf" 'Geo backup must be disabled.'
assert_present 'public_network_access_enabled[[:space:]]*=[[:space:]]*true' "$MODULE_DIR/main.tf" 'Explicit public networking is required.'
assert_present 'password_auth_enabled[[:space:]]*=[[:space:]]*false' "$MODULE_DIR/main.tf" 'Password authentication must be disabled.'
printf '%s\n' "$SERVER_BLOCK" | grep -Eq 'prevent_destroy[[:space:]]*=[[:space:]]*true' || fail 'The PostgreSQL server requires destruction protection.'
assert_present 'value[[:space:]]*=[[:space:]]*"TLSv1\.2"' "$MODULE_DIR/main.tf" 'TLS 1.2 is required.'
assert_present 'name[[:space:]]*=[[:space:]]*"messagebridge_dev"' "$SHARED_DIR/locals.tf" 'Development database is required.'
assert_present 'name[[:space:]]*=[[:space:]]*"messagebridge_prod"' "$SHARED_DIR/locals.tf" 'Production database is required.'

assert_absent 'high_availability[[:space:]]*\{' 'High availability blocks are forbidden.' "${TF_FILES[@]}"
assert_absent 'geo_redundant_backup_enabled[[:space:]]*=[[:space:]]*true' 'Geo-redundant backup is forbidden.' "${TF_FILES[@]}"
assert_absent '(delegated_subnet_id|private_dns_zone_id)[[:space:]]*=' 'Private server networking is forbidden.' "${TF_FILES[@]}"
assert_absent 'resource[[:space:]]+"azurerm_(virtual_network|subnet|private_endpoint)"' 'VNet and private endpoint resources are forbidden.' "${TF_FILES[@]}"
assert_absent 'resource[[:space:]]+"azurerm_(log_analytics_workspace|monitor_diagnostic_setting)"' 'Log Analytics resources are forbidden.' "${TF_FILES[@]}"
assert_absent '(start_ip_address|end_ip_address)[[:space:]]*=[[:space:]]*"0\.0\.0\.0"' 'Broad Azure-services firewall access is forbidden.' "${TF_FILES[@]}"
assert_absent 'administrator_(login|password)' 'Password administrator fields are forbidden.' "${TF_FILES[@]}"
assert_absent 'password_auth_enabled[[:space:]]*=[[:space:]]*true' 'Password authentication is forbidden.' "${TF_FILES[@]}"
assert_absent 'variable[[:space:]]+"[^"]*(password|secret|token|credential|connection)[^"]*"' 'Secret-bearing inputs are forbidden.' "${TF_FILES[@]}"

printf '%s\n' 'Database policy contract checks passed.'

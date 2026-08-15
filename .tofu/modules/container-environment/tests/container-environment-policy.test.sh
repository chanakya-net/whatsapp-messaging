#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
MODULE_DIR="$REPO_ROOT/.tofu/modules/container-environment"
DEV_DIR="$REPO_ROOT/.tofu/envs/dev"
PROD_DIR="$REPO_ROOT/.tofu/envs/prod"

MODULE_FILES=("$MODULE_DIR"/*.tf)
DEV_FILES=("$DEV_DIR"/*.tf)
PROD_FILES=("$PROD_DIR"/*.tf)
ALL_FILES=("${MODULE_FILES[@]}" "${DEV_FILES[@]}" "${PROD_FILES[@]}")

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

assert_present() {
  local pattern="$1" path="$2" message="$3"
  grep -Eq "$pattern" "$path" || fail "$message"
}

assert_absent() {
  local pattern="$1" message="$2"
  shift 2
  if grep -Ein "$pattern" "$@"; then
    fail "$message"
  fi
}

assert_count() {
  local expected="$1" pattern="$2" message="$3"
  shift 3
  local actual
  actual="$(grep -Eh "$pattern" "$@" | wc -l | tr -d ' ')"
  [[ "$actual" == "$expected" ]] || fail "$message (expected $expected, got $actual)"
}

assert_consumption_only_environment() {
  assert_count 1 '^resource "azurerm_container_app_environment" "this"' \
    'The module must declare exactly one managed environment.' "${MODULE_FILES[@]}"
  assert_present 'workload_profile_type[[:space:]]*=[[:space:]]*"Consumption"' \
    "$MODULE_DIR/main.tf" 'The managed environment must use the Consumption workload profile type.'
  assert_present 'name[[:space:]]*=[[:space:]]*"Consumption"' \
    "$MODULE_DIR/main.tf" 'The single workload profile must be named Consumption.'
  assert_count 1 'workload_profile[[:space:]]*\{' \
    'Exactly one workload profile block is allowed.' "${ALL_FILES[@]}"
  assert_count 1 'workload_profile_type[[:space:]]*=' \
    'Every workload profile type assignment must be the Consumption profile.' "${ALL_FILES[@]}"
  assert_absent '(minimum_count|maximum_count)[[:space:]]*=' \
    'Dedicated instance counts are forbidden on Consumption.' "${ALL_FILES[@]}"
  assert_present 'var.location == "centralindia"' \
    "$MODULE_DIR/variables.tf" 'The module must reject regions other than Central India.'
  assert_present 'location[[:space:]]*=[[:space:]]*"centralindia"' \
    "$DEV_DIR/locals.tf" 'Dev must stay in Central India.'
  assert_present 'location[[:space:]]*=[[:space:]]*"centralindia"' \
    "$PROD_DIR/locals.tf" 'Prod must stay in Central India.'
}

assert_no_cost_bearing_infrastructure() {
  assert_absent 'resource[[:space:]]+"azurerm_container_registry' \
    'Container registries are forbidden; images come from public GHCR.' "${ALL_FILES[@]}"
  assert_absent 'resource[[:space:]]+"azurerm_(virtual_network|subnet|network_security_group|nat_gateway|public_ip|private_endpoint|private_dns_zone)' \
    'Virtual network, NAT, public IP, and private endpoint resources are forbidden.' "${ALL_FILES[@]}"
  assert_absent 'resource[[:space:]]+"azurerm_(log_analytics_workspace|application_insights|monitor_diagnostic_setting)' \
    'Log Analytics and Application Insights resources are forbidden.' "${ALL_FILES[@]}"
  assert_absent '(log_analytics_workspace_id|logs_destination|dapr_application_insights_connection_string)[[:space:]]*=' \
    'Log Analytics and Application Insights wiring is forbidden.' "${ALL_FILES[@]}"
  assert_absent '(infrastructure_subnet_id|infrastructure_resource_group_name|internal_load_balancer_enabled|zone_redundancy_enabled|mutual_tls_enabled)[[:space:]]*=' \
    'Networked and zone-redundant environment add-ons are forbidden.' "${ALL_FILES[@]}"
}

assert_no_public_workload_exposure() {
  assert_absent 'resource[[:space:]]+"azurerm_container_app(_job|_custom_domain)?"' \
    'Workload and job resources belong to later slices, not this environment slice.' "${ALL_FILES[@]}"
  assert_absent '(ingress[[:space:]]*\{|external_enabled[[:space:]]*=|allow_insecure_connections[[:space:]]*=)' \
    'Public workload ingress is forbidden in this slice.' "${ALL_FILES[@]}"
  assert_absent 'public_network_access[[:space:]]*=' \
    'The managed environment must keep the provider default public network access setting.' "${MODULE_FILES[@]}"
}

assert_isolated_identities() {
  assert_count 2 '^resource "azurerm_user_assigned_identity"' \
    'Dev must declare exactly the runtime and migrator identities.' "${DEV_FILES[@]}"
  assert_count 2 '^resource "azurerm_user_assigned_identity"' \
    'Prod must declare exactly the runtime and migrator identities.' "${PROD_FILES[@]}"
  assert_present '^resource "azurerm_user_assigned_identity" "runtime"' "$DEV_DIR/identities.tf" 'Dev requires a runtime identity.'
  assert_present '^resource "azurerm_user_assigned_identity" "migrator"' "$DEV_DIR/identities.tf" 'Dev requires a migrator identity.'
  assert_present '^resource "azurerm_user_assigned_identity" "runtime"' "$PROD_DIR/identities.tf" 'Prod requires a runtime identity.'
  assert_present '^resource "azurerm_user_assigned_identity" "migrator"' "$PROD_DIR/identities.tf" 'Prod requires a migrator identity.'

  assert_absent 'resource[[:space:]]+"azurerm_(role_assignment|role_definition|federated_identity_credential)"' \
    'Environment roots must create no role assignments in this slice.' "${ALL_FILES[@]}"
  assert_absent 'azurerm_user_assigned_identity\.migrator' \
    'The migrator identity must never be wired into the Key Vault module.' "$DEV_DIR/key-vault.tf" "$PROD_DIR/key-vault.tf"

  assert_count 1 '^module "container_environment"' 'Dev must call the environment module exactly once.' "${DEV_FILES[@]}"
  assert_count 1 '^module "container_environment"' 'Prod must call the environment module exactly once.' "${PROD_FILES[@]}"
}

assert_no_cross_environment_references() {
  assert_absent '(^|[^a-z])prod([^a-z]|$)' 'Dev sources must never reference prod resources.' "${DEV_FILES[@]}"
  assert_absent '(^|[^a-z])dev([^a-z]|$)' 'Prod sources must never reference dev resources.' "${PROD_FILES[@]}"
  assert_absent '(^|[^a-z])(dev|prod)([^a-z]|$)' 'The shared module must stay environment agnostic.' "${MODULE_FILES[@]}"
}

assert_consumption_only_environment
assert_no_cost_bearing_infrastructure
assert_no_public_workload_exposure
assert_isolated_identities
assert_no_cross_environment_references

printf '%s\n' 'Container Apps environment isolation and cost policy checks passed.'

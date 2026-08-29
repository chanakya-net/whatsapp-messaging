#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
ENVIRONMENT_DIR="$REPO_ROOT/.tofu/envs/prod"
CATALOG_FILE="$REPO_ROOT/.tofu/modules/metric-alerts/catalog.tf"
SOURCES=("$ENVIRONMENT_DIR"/*.tf "$REPO_ROOT/.tofu/modules/metric-alerts"/*.tf)

FORBIDDEN_RESOURCES='resource[[:space:]]+"azurerm_(log_analytics_workspace|application_insights[^"]*|monitor_workspace|monitor_data_collection_(endpoint|rule)|dashboard_grafana|prometheus_rule_group|monitor_scheduled_query_rules[^"]*|monitor_alert_processing_rule[^"]*|monitor_smart_detector[^"]*|monitor_activity_log_alert)"'
if grep -En "$FORBIDDEN_RESOURCES" "${SOURCES[@]}"; then
  printf '%s\n' 'FAIL: Prod alerts must not provision log analytics, application insights, query alerts, processing rules, or managed Prometheus.' >&2
  exit 1
fi

if grep -En '(log_analytics_workspace_id|logs_destination|dapr_application_insights_connection_string|prometheus)[[:space:]]*=' "${SOURCES[@]}"; then
  printf '%s\n' 'FAIL: Prod must not wire Azure-native telemetry sinks or Prometheus.' >&2
  exit 1
fi

EXPECTED_METRICS="$(printf '%s\n' Executions Replicas RestartCount WorkingSetBytes active_connections cpu_credits_remaining cpu_percent is_db_alive storage_percent | LC_ALL=C sort | tr '\n' ' ')"
ACTUAL_METRICS="$(sed -nE 's/^[[:space:]]*metric_name[[:space:]]*=[[:space:]]*"([^"]+)".*/\1/p' "$CATALOG_FILE" | LC_ALL=C sort -u | tr '\n' ' ')"
if [[ "$ACTUAL_METRICS" != "$EXPECTED_METRICS" ]]; then
  printf 'FAIL: Reviewed native metric allowlist mismatch. Got: %s\n' "$ACTUAL_METRICS" >&2
  exit 1
fi

if grep -En '"(JobExecutions|Status|OOMKilled|OutOfMemory)"' "$CATALOG_FILE"; then
  printf '%s\n' 'FAIL: Catalog promises an unsupported metric or dimension.' >&2
  exit 1
fi

ACTION_GROUP_COUNT="$(grep -Ehc 'resource[[:space:]]+"azurerm_monitor_action_group"' "$ENVIRONMENT_DIR"/*.tf | awk '{ total += $1 } END { print total + 0 }')"
if [[ "$ACTION_GROUP_COUNT" != "1" ]]; then
  printf 'FAIL: Prod must define one Action Group; found %s declarations.\n' "$ACTION_GROUP_COUNT" >&2
  exit 1
fi

printf '%s\n' 'Prod Azure-native alert cost-policy checks passed.'

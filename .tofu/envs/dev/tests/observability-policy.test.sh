#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
ENVIRONMENT_FILES=("$REPO_ROOT/.tofu/envs/dev"/*.tf)

if rg -n 'resource[[:space:]]+"azurerm_(log_analytics_workspace|application_insights|monitor_workspace|monitor_data_collection_(endpoint|rule)|dashboard_grafana|prometheus_rule_group)"' "${ENVIRONMENT_FILES[@]}"; then
  printf '%s\n' 'FAIL: Dev must not provision Log Analytics, Application Insights, or Prometheus-managed resources.' >&2
  exit 1
fi

if rg -n '(log_analytics_workspace_id|logs_destination|dapr_application_insights_connection_string|prometheus)[[:space:]]*=' "${ENVIRONMENT_FILES[@]}"; then
  printf '%s\n' 'FAIL: Dev must not wire Azure-native telemetry sinks or Prometheus.' >&2
  exit 1
fi

printf '%s\n' 'Dev direct New Relic telemetry policy checks passed.'

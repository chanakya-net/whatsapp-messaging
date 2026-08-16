output "alert_definitions" {
  description = "Reviewed native metric alert definitions keyed by resource kind, scope, and metric."
  value = {
    for key, alert in merge(local.postgres_alerts, local.container_app_alerts, local.job_alerts) : key => {
      name             = alert.name
      metric_namespace = alert.metric_namespace
      metric_name      = alert.metric_name
      aggregation      = alert.aggregation
      operator         = alert.operator
      threshold        = alert.threshold
      severity         = alert.severity
      frequency        = "PT5M"
      window_size      = "PT15M"
      scope_id         = alert.scope_id
      dimensions       = try(alert.dimensions, {})
    }
  }
}

output "alert_ids" {
  description = "Azure Monitor metric alert resource IDs keyed like alert_definitions."
  value = merge(
    { for key, alert in azurerm_monitor_metric_alert.postgres : key => alert.id },
    { for key, alert in azurerm_monitor_metric_alert.container_app : key => alert.id },
    { for key, alert in azurerm_monitor_metric_alert.job : key => alert.id },
  )
}

output "metric_catalog" {
  description = "Reviewed Azure-native metric inventory and source API versions."
  value       = local.metric_catalog
}

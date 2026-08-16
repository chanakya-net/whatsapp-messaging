resource "azurerm_monitor_action_group" "alerts" {
  name                = "ag-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  resource_group_name = local.resource_group_name
  short_name          = "msgbr-alerts"
  tags                = merge(local.mandatory_tags, { component = "alerting" })

  email_receiver {
    name                    = "platform-email"
    email_address           = var.alert_email
    use_common_alert_schema = true
  }
}

module "metric_alerts" {
  source = "../../modules/metric-alerts"

  resource_group_name = local.resource_group_name
  action_group_id     = "/subscriptions/${var.subscription_id}/resourceGroups/${local.resource_group_name}/providers/Microsoft.Insights/actionGroups/${azurerm_monitor_action_group.alerts.name}"
  postgres_scopes = {
    primary = {
      resource_id = one(module.database.alertable_resource_ids)
    }
  }
  tags = merge(local.mandatory_tags, { component = "alerting" })
}

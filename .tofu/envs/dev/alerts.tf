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
  container_app_scopes = {
    worker = {
      resource_id = one(module.worker.alertable_resource_ids)
    }
  }
  job_scopes = {
    migration = {
      resource_id = module.worker.migration_job_id
      severity    = 1
    }
    smoke = {
      resource_id = module.worker.smoke_job_id
      severity    = 2
    }
  }
  tags = merge(local.mandatory_tags, { component = "alerting" })
}

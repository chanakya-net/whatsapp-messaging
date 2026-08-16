mock_provider "azurerm" {
  mock_resource "azurerm_postgresql_flexible_server" {
    defaults = {
      id                  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-shared-centralindia-042/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-messagebridge-shared-cin-042"
      fqdn                = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
      private_dns_zone_id = null
    }
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  alert_email      = "platform-alerts@example.com"
  entra_administrator = {
    object_id      = "00000000-0000-4000-8000-000000000003"
    principal_name = "messagebridge-shared-operators"
    principal_type = "Group"
  }
  reviewed_egress_ranges = {
    ip-20-192-0-20 = "20.192.0.20/32"
  }
}

run "wires_one_email_action_group_and_database_alerts" {
  command = plan

  assert {
    condition = (
      azurerm_monitor_action_group.alerts.name == "ag-messagebridge-shared-cin-042" &&
      azurerm_monitor_action_group.alerts.short_name == "msgbr-alerts" &&
      length(azurerm_monitor_action_group.alerts.email_receiver) == 1 &&
      azurerm_monitor_action_group.alerts.email_receiver[0].email_address == var.alert_email &&
      azurerm_monitor_action_group.alerts.email_receiver[0].use_common_alert_schema == true
    )
    error_message = "Shared must own exactly one common-schema email Action Group."
  }

  assert {
    condition = (
      length(module.metric_alerts.alert_definitions) == 5 &&
      alltrue([
        for alert in values(module.metric_alerts.alert_definitions) :
        alert.scope_id == module.database.server_id &&
        alert.metric_namespace == "Microsoft.DBforPostgreSQL/flexibleServers"
      ])
    )
    error_message = "Shared must attach all five reviewed PostgreSQL alerts to its database."
  }
}

run "invalid_alert_email_stops" {
  command = plan

  variables {
    alert_email = "not-an-email"
  }

  expect_failures = [var.alert_email]
}

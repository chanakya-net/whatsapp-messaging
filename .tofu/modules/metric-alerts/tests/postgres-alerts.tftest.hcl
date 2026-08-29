mock_provider "azurerm" {}

variables {
  resource_group_name = "rg-messagebridge-shared-centralindia-042"
  action_group_id     = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-shared-centralindia-042/providers/Microsoft.Insights/actionGroups/ag-messagebridge-shared-cin-042"
  postgres_scopes = {
    shared = {
      resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-shared-centralindia-042/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-messagebridge-shared-cin-042"
    }
  }
}

run "creates_reviewed_postgres_alerts" {
  command = plan

  assert {
    condition = toset(keys(output.alert_definitions)) == toset([
      "postgres.shared.active_connections",
      "postgres.shared.cpu_credits_remaining",
      "postgres.shared.cpu_percent",
      "postgres.shared.is_db_alive",
      "postgres.shared.storage_percent",
    ])
    error_message = "Each PostgreSQL scope must receive exactly the five reviewed native metric alerts."
  }

  assert {
    condition     = toset(keys(output.alert_ids)) == toset(keys(output.alert_definitions))
    error_message = "Alert IDs must use the same stable keys as reviewed definitions."
  }

  assert {
    condition = (
      output.alert_definitions["postgres.shared.cpu_percent"].metric_name == "cpu_percent" &&
      output.alert_definitions["postgres.shared.cpu_percent"].aggregation == "Average" &&
      output.alert_definitions["postgres.shared.cpu_percent"].operator == "GreaterThan" &&
      output.alert_definitions["postgres.shared.cpu_percent"].threshold == 80 &&
      output.alert_definitions["postgres.shared.cpu_percent"].severity == 2
    )
    error_message = "PostgreSQL CPU alert must preserve the reviewed saturation default."
  }

  assert {
    condition = (
      output.alert_definitions["postgres.shared.cpu_credits_remaining"].operator == "LessThan" &&
      output.alert_definitions["postgres.shared.cpu_credits_remaining"].threshold == 30 &&
      output.alert_definitions["postgres.shared.active_connections"].threshold == 40 &&
      output.alert_definitions["postgres.shared.storage_percent"].threshold == 80 &&
      output.alert_definitions["postgres.shared.storage_percent"].severity == 1
    )
    error_message = "PostgreSQL credit, connection, and storage alerts must preserve reviewed defaults."
  }

  assert {
    condition = (
      output.alert_definitions["postgres.shared.is_db_alive"].aggregation == "Maximum" &&
      output.alert_definitions["postgres.shared.is_db_alive"].operator == "LessThan" &&
      output.alert_definitions["postgres.shared.is_db_alive"].threshold == 1 &&
      output.alert_definitions["postgres.shared.is_db_alive"].severity == 0
    )
    error_message = "PostgreSQL availability must alert when the maximum alive signal stays below one."
  }

  assert {
    condition = alltrue([
      for alert in values(output.alert_definitions) :
      alert.metric_namespace == "Microsoft.DBforPostgreSQL/flexibleServers" &&
      alert.frequency == "PT5M" &&
      alert.window_size == "PT15M"
    ])
    error_message = "PostgreSQL alerts must use the reviewed namespace and low-frequency static evaluation."
  }
}

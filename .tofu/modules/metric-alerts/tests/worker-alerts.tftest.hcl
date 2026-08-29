mock_provider "azurerm" {}

variables {
  resource_group_name = "rg-messagebridge-dev-centralindia-042"
  action_group_id     = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.Insights/actionGroups/ag-messagebridge-dev-cin-042"
  container_app_scopes = {
    worker = {
      resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/containerApps/ca-messagebridge-dev-cin-042"
    }
  }
}

run "creates_reviewed_worker_alerts" {
  command = plan

  assert {
    condition = toset(keys(output.alert_definitions)) == toset([
      "container_app.worker.Replicas",
      "container_app.worker.RestartCount",
      "container_app.worker.WorkingSetBytes",
    ])
    error_message = "Each worker scope must receive exactly the three reviewed Container Apps alerts."
  }

  assert {
    condition = (
      output.alert_definitions["container_app.worker.Replicas"].aggregation == "Minimum" &&
      output.alert_definitions["container_app.worker.Replicas"].operator == "LessThan" &&
      output.alert_definitions["container_app.worker.Replicas"].threshold == 1 &&
      output.alert_definitions["container_app.worker.Replicas"].severity == 1
    )
    error_message = "Worker availability must alert when minimum running replicas falls below one."
  }

  assert {
    condition = (
      output.alert_definitions["container_app.worker.RestartCount"].aggregation == "Maximum" &&
      output.alert_definitions["container_app.worker.RestartCount"].operator == "GreaterThan" &&
      output.alert_definitions["container_app.worker.RestartCount"].threshold == 3 &&
      output.alert_definitions["container_app.worker.RestartCount"].severity == 2
    )
    error_message = "Worker restart indicator must preserve the reviewed static threshold."
  }

  assert {
    condition = (
      output.alert_definitions["container_app.worker.WorkingSetBytes"].aggregation == "Average" &&
      output.alert_definitions["container_app.worker.WorkingSetBytes"].threshold == 966367642 &&
      output.alert_definitions["container_app.worker.WorkingSetBytes"].severity == 2
    )
    error_message = "Worker memory alert must remain an explicit 90%-of-1GiB OOM-risk proxy."
  }

  assert {
    condition = alltrue([
      for alert in values(output.alert_definitions) :
      alert.metric_namespace == "Microsoft.App/containerapps" &&
      alert.frequency == "PT5M" &&
      alert.window_size == "PT15M"
    ])
    error_message = "Worker alerts must use only reviewed native Container Apps metrics."
  }
}

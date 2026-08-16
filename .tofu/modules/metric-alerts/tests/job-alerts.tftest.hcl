mock_provider "azurerm" {}

variables {
  resource_group_name = "rg-messagebridge-dev-centralindia-042"
  action_group_id     = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.Insights/actionGroups/ag-messagebridge-dev-cin-042"
  job_scopes = {
    migration = {
      resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/jobs/mig-messagebridge-dev-cin-042"
      severity    = 1
    }
    smoke = {
      resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/jobs/smoke-messagebridge-dev-cin-042"
      severity    = 2
    }
  }
}

run "creates_native_failed_job_alerts" {
  command = plan

  assert {
    condition = toset(keys(output.alert_definitions)) == toset([
      "job.migration.Executions",
      "job.smoke.Executions",
    ])
    error_message = "Each job scope must receive one native failed-execution alert."
  }

  assert {
    condition = alltrue([
      for alert in values(output.alert_definitions) :
      alert.metric_namespace == "Microsoft.App/jobs" &&
      alert.metric_name == "Executions" &&
      alert.aggregation == "Total" &&
      alert.operator == "GreaterThanOrEqual" &&
      alert.threshold == 1 &&
      alert.dimensions == { state = ["Failed"] }
    ])
    error_message = "Job failures must use Executions with the documented state=Failed dimension."
  }

  assert {
    condition = (
      output.alert_definitions["job.migration.Executions"].severity == 1 &&
      output.alert_definitions["job.smoke.Executions"].severity == 2
    )
    error_message = "Job scopes must retain their reviewed per-job severities."
  }

  assert {
    condition = (
      output.metric_catalog["Microsoft.App/jobs"].resource_api_version == "2024-03-01" &&
      toset(keys(output.metric_catalog["Microsoft.App/jobs"].metrics)) == toset(["Executions"]) &&
      output.metric_catalog["Microsoft.App/jobs"].metrics.Executions.dimensions == toset(["state", "jobName", "executionName"])
    )
    error_message = "The selected jobs API catalog must contain only the reviewed native metric promise."
  }
}

run "invalid_action_group_id_stops" {
  command = plan

  variables {
    action_group_id = " "
  }

  expect_failures = [var.action_group_id]
}

run "invalid_job_threshold_stops" {
  command = plan

  variables {
    job_thresholds = {
      failed_executions = 0
    }
  }

  expect_failures = [var.job_thresholds]
}

run "invalid_job_severity_stops" {
  command = plan

  variables {
    job_scopes = {
      migration = {
        resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/jobs/mig-messagebridge-dev-cin-042"
        severity    = 5
      }
    }
  }

  expect_failures = [var.job_scopes]
}

run "wrong_job_scope_type_stops" {
  command = plan

  variables {
    job_scopes = {
      worker = {
        resource_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/containerApps/ca-messagebridge-dev-cin-042"
        severity    = 2
      }
    }
  }

  expect_failures = [azurerm_monitor_metric_alert.job]
}

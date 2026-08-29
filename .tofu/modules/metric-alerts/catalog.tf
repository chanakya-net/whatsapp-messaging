locals {
  postgres_namespace      = "Microsoft.DBforPostgreSQL/flexibleServers"
  container_app_namespace = "Microsoft.App/containerapps"
  job_namespace           = "Microsoft.App/jobs"

  # Reviewed against Azure Monitor metric definitions and the listed resource API version.
  # Only this allowlist may be used by alert resources in this module.
  metric_catalog = {
    (local.postgres_namespace) = {
      resource_api_version           = "2024-08-01"
      metric_definitions_api_version = "2018-01-01"
      metrics = {
        active_connections = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["ServerName"])
          time_grains  = toset(["PT1M"])
        }
        cpu_credits_remaining = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["LogicalServerName"])
          time_grains  = toset(["PT1M"])
        }
        cpu_percent = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["ServerName"])
          time_grains  = toset(["PT1M"])
        }
        is_db_alive = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["ServerName"])
          time_grains  = toset(["PT1M"])
        }
        storage_percent = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["ServerName"])
          time_grains  = toset(["PT1M"])
        }
      }
    }
    (local.container_app_namespace) = {
      resource_api_version           = "2024-03-01"
      metric_definitions_api_version = "2018-01-01"
      metrics = {
        Replicas = {
          aggregations = toset(["Average", "Maximum", "Minimum"])
          dimensions   = toset(["revisionName"])
          time_grains  = toset(["PT1M"])
        }
        RestartCount = {
          aggregations = toset(["Average", "Total", "Maximum", "Minimum"])
          dimensions   = toset(["revisionName", "podName"])
          time_grains  = toset(["PT1M"])
        }
        WorkingSetBytes = {
          aggregations = toset(["Average", "Total", "Maximum", "Minimum"])
          dimensions   = toset(["revisionName", "podName"])
          time_grains  = toset(["PT1M"])
        }
      }
    }
    (local.job_namespace) = {
      resource_api_version           = "2024-03-01"
      metric_definitions_api_version = "2018-01-01"
      metrics = {
        Executions = {
          aggregations = toset(["Average", "Total", "Maximum", "Minimum"])
          dimensions   = toset(["state", "jobName", "executionName"])
          time_grains  = toset(["PT1M"])
        }
      }
    }
  }

  postgres_specs = {
    active_connections = {
      metric_name = "active_connections"
      aggregation = "Average"
      operator    = "GreaterThan"
      threshold   = var.postgres_thresholds.active_connections
      severity    = var.postgres_severities.active_connections
      description = "PostgreSQL active connections are nearing the B1ms connection ceiling."
    }
    cpu_credits_remaining = {
      metric_name = "cpu_credits_remaining"
      aggregation = "Average"
      operator    = "LessThan"
      threshold   = var.postgres_thresholds.cpu_credits_remaining
      severity    = var.postgres_severities.cpu_credits_remaining
      description = "PostgreSQL burst CPU credits are nearly exhausted."
    }
    cpu_percent = {
      metric_name = "cpu_percent"
      aggregation = "Average"
      operator    = "GreaterThan"
      threshold   = var.postgres_thresholds.cpu_percent
      severity    = var.postgres_severities.cpu_percent
      description = "PostgreSQL CPU is persistently saturated."
    }
    is_db_alive = {
      metric_name = "is_db_alive"
      aggregation = "Maximum"
      operator    = "LessThan"
      threshold   = var.postgres_thresholds.is_db_alive
      severity    = var.postgres_severities.is_db_alive
      description = "PostgreSQL did not report alive during the evaluation window."
    }
    storage_percent = {
      metric_name = "storage_percent"
      aggregation = "Average"
      operator    = "GreaterThan"
      threshold   = var.postgres_thresholds.storage_percent
      severity    = var.postgres_severities.storage_percent
      description = "PostgreSQL fixed storage is nearing capacity."
    }
  }

  postgres_alerts = merge([
    for scope_key, scope in var.postgres_scopes : {
      for metric_key, spec in local.postgres_specs :
      "postgres.${scope_key}.${metric_key}" => merge(spec, {
        name             = "alert-${scope_key}-postgres-${replace(metric_key, "_", "-")}"
        metric_namespace = local.postgres_namespace
        scope_id         = scope.resource_id
      })
    }
  ]...)

  container_app_specs = {
    Replicas = {
      metric_name = "Replicas"
      aggregation = "Minimum"
      operator    = "LessThan"
      threshold   = var.container_app_thresholds.running_replicas
      severity    = var.container_app_severities.running_replicas
      description = "The fixed single-replica worker has no running replica."
    }
    RestartCount = {
      metric_name = "RestartCount"
      aggregation = "Maximum"
      operator    = "GreaterThan"
      threshold   = var.container_app_thresholds.restart_count
      severity    = var.container_app_severities.restart_count
      description = "The worker replica restart counter indicates repeated restarts."
    }
    WorkingSetBytes = {
      metric_name = "WorkingSetBytes"
      aggregation = "Average"
      operator    = "GreaterThan"
      threshold   = var.container_app_thresholds.working_set_bytes
      severity    = var.container_app_severities.working_set_bytes
      description = "Worker memory exceeds the reviewed OOM-risk proxy threshold."
    }
  }

  container_app_alerts = merge([
    for scope_key, scope in var.container_app_scopes : {
      for metric_key, spec in local.container_app_specs :
      "container_app.${scope_key}.${metric_key}" => merge(spec, {
        name             = "alert-${scope_key}-worker-${lower(metric_key)}"
        metric_namespace = local.container_app_namespace
        scope_id         = scope.resource_id
      })
    }
  ]...)

  job_alerts = {
    for scope_key, scope in var.job_scopes :
    "job.${scope_key}.Executions" => {
      name             = "alert-${scope_key}-job-failed"
      metric_namespace = local.job_namespace
      metric_name      = "Executions"
      aggregation      = "Total"
      operator         = "GreaterThanOrEqual"
      threshold        = var.job_thresholds.failed_executions
      severity         = scope.severity
      description      = "A Container Apps job execution entered the Failed state."
      scope_id         = scope.resource_id
      dimensions       = { state = ["Failed"] }
    }
  }
}

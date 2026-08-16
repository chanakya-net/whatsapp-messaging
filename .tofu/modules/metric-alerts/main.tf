resource "azurerm_monitor_metric_alert" "postgres" {
  for_each = local.postgres_alerts

  name                = each.value.name
  resource_group_name = var.resource_group_name
  scopes              = [each.value.scope_id]
  description         = each.value.description
  severity            = each.value.severity
  frequency           = "PT5M"
  window_size         = "PT15M"
  auto_mitigate       = true
  enabled             = true
  tags                = var.tags

  criteria {
    metric_namespace = each.value.metric_namespace
    metric_name      = each.value.metric_name
    aggregation      = each.value.aggregation
    operator         = each.value.operator
    threshold        = each.value.threshold
  }

  action {
    action_group_id = var.action_group_id
  }

  lifecycle {
    precondition {
      condition     = strcontains(lower(each.value.scope_id), "/providers/${lower(each.value.metric_namespace)}/")
      error_message = "PostgreSQL alert scopes must be Microsoft.DBforPostgreSQL/flexibleServers resource IDs."
    }

    precondition {
      condition = (
        contains(keys(local.metric_catalog[each.value.metric_namespace].metrics), each.value.metric_name) &&
        contains(local.metric_catalog[each.value.metric_namespace].metrics[each.value.metric_name].aggregations, each.value.aggregation)
      )
      error_message = "Every PostgreSQL metric and aggregation must exist in the reviewed native metric catalog."
    }
  }
}

resource "azurerm_monitor_metric_alert" "container_app" {
  for_each = local.container_app_alerts

  name                = each.value.name
  resource_group_name = var.resource_group_name
  scopes              = [each.value.scope_id]
  description         = each.value.description
  severity            = each.value.severity
  frequency           = "PT5M"
  window_size         = "PT15M"
  auto_mitigate       = true
  enabled             = true
  tags                = var.tags

  criteria {
    metric_namespace = each.value.metric_namespace
    metric_name      = each.value.metric_name
    aggregation      = each.value.aggregation
    operator         = each.value.operator
    threshold        = each.value.threshold
  }

  action {
    action_group_id = var.action_group_id
  }

  lifecycle {
    precondition {
      condition     = strcontains(lower(each.value.scope_id), "/providers/${lower(each.value.metric_namespace)}/")
      error_message = "Worker alert scopes must be Microsoft.App/containerApps resource IDs."
    }

    precondition {
      condition = (
        contains(keys(local.metric_catalog[each.value.metric_namespace].metrics), each.value.metric_name) &&
        contains(local.metric_catalog[each.value.metric_namespace].metrics[each.value.metric_name].aggregations, each.value.aggregation)
      )
      error_message = "Every worker metric and aggregation must exist in the reviewed native metric catalog."
    }
  }
}

resource "azurerm_monitor_metric_alert" "job" {
  for_each = local.job_alerts

  name                = each.value.name
  resource_group_name = var.resource_group_name
  scopes              = [each.value.scope_id]
  description         = each.value.description
  severity            = each.value.severity
  frequency           = "PT5M"
  window_size         = "PT15M"
  auto_mitigate       = true
  enabled             = true
  tags                = var.tags

  criteria {
    metric_namespace = each.value.metric_namespace
    metric_name      = each.value.metric_name
    aggregation      = each.value.aggregation
    operator         = each.value.operator
    threshold        = each.value.threshold

    dimension {
      name     = "state"
      operator = "Include"
      values   = ["Failed"]
    }
  }

  action {
    action_group_id = var.action_group_id
  }

  lifecycle {
    precondition {
      condition     = strcontains(lower(each.value.scope_id), "/providers/${lower(each.value.metric_namespace)}/")
      error_message = "Job alert scopes must be Microsoft.App/jobs resource IDs."
    }

    precondition {
      condition = (
        contains(keys(local.metric_catalog[each.value.metric_namespace].metrics), each.value.metric_name) &&
        contains(local.metric_catalog[each.value.metric_namespace].metrics[each.value.metric_name].aggregations, each.value.aggregation) &&
        contains(local.metric_catalog[each.value.metric_namespace].metrics[each.value.metric_name].dimensions, "state")
      )
      error_message = "Every job metric, aggregation, and dimension must exist in the reviewed native metric catalog."
    }
  }
}

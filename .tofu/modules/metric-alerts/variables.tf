variable "resource_group_name" {
  description = "Resource group that owns the Azure Monitor metric alerts."
  type        = string

  validation {
    condition     = trimspace(var.resource_group_name) != ""
    error_message = "resource_group_name must not be blank."
  }
}

variable "action_group_id" {
  description = "Resource ID of the single email Action Group receiving every alert."
  type        = string

  validation {
    condition     = can(regex("(?i)^/subscriptions/[0-9a-f-]+/resourceGroups/[^/]+/providers/Microsoft\\.Insights/actionGroups/[^/]+$", var.action_group_id))
    error_message = "action_group_id must be a complete Microsoft.Insights/actionGroups resource ID."
  }
}

variable "postgres_scopes" {
  description = "PostgreSQL Flexible Server scopes keyed by stable alert-name token."
  type = map(object({
    resource_id = string
  }))
  default = {}
}

variable "postgres_thresholds" {
  description = "Reviewed PostgreSQL metric thresholds."
  type = object({
    cpu_percent           = optional(number, 80)
    cpu_credits_remaining = optional(number, 30)
    active_connections    = optional(number, 40)
    storage_percent       = optional(number, 80)
    is_db_alive           = optional(number, 1)
  })
  default = {}

  validation {
    condition = (
      var.postgres_thresholds.cpu_percent > 0 && var.postgres_thresholds.cpu_percent <= 100 &&
      var.postgres_thresholds.cpu_credits_remaining >= 0 &&
      var.postgres_thresholds.active_connections > 0 &&
      var.postgres_thresholds.storage_percent > 0 && var.postgres_thresholds.storage_percent <= 100 &&
      var.postgres_thresholds.is_db_alive > 0 && var.postgres_thresholds.is_db_alive <= 1
    )
    error_message = "PostgreSQL thresholds must stay within their documented metric ranges."
  }
}

variable "postgres_severities" {
  description = "Azure Monitor severities for PostgreSQL alerts (0 is highest, 4 is lowest)."
  type = object({
    cpu_percent           = optional(number, 2)
    cpu_credits_remaining = optional(number, 2)
    active_connections    = optional(number, 2)
    storage_percent       = optional(number, 1)
    is_db_alive           = optional(number, 0)
  })
  default = {}

  validation {
    condition     = alltrue([for severity in values(var.postgres_severities) : severity >= 0 && severity <= 4 && floor(severity) == severity])
    error_message = "PostgreSQL severities must be whole numbers from 0 through 4."
  }
}

variable "container_app_scopes" {
  description = "Container App worker scopes keyed by stable alert-name token."
  type = map(object({
    resource_id = string
  }))
  default = {}
}

variable "container_app_thresholds" {
  description = "Reviewed Container App worker thresholds."
  type = object({
    running_replicas  = optional(number, 1)
    restart_count     = optional(number, 3)
    working_set_bytes = optional(number, 966367642)
  })
  default = {}

  validation {
    condition = (
      var.container_app_thresholds.running_replicas == 1 &&
      var.container_app_thresholds.restart_count >= 0 &&
      var.container_app_thresholds.working_set_bytes > 0
    )
    error_message = "Worker thresholds require one fixed replica and non-negative resource indicators."
  }
}

variable "container_app_severities" {
  description = "Azure Monitor severities for worker alerts."
  type = object({
    running_replicas  = optional(number, 1)
    restart_count     = optional(number, 2)
    working_set_bytes = optional(number, 2)
  })
  default = {}

  validation {
    condition     = alltrue([for severity in values(var.container_app_severities) : severity >= 0 && severity <= 4 && floor(severity) == severity])
    error_message = "Container App severities must be whole numbers from 0 through 4."
  }
}

variable "job_scopes" {
  description = "Container Apps Job scopes and reviewed severities keyed by stable alert-name token."
  type = map(object({
    resource_id = string
    severity    = optional(number, 2)
  }))
  default = {}

  validation {
    condition     = alltrue([for scope in values(var.job_scopes) : scope.severity >= 0 && scope.severity <= 4 && floor(scope.severity) == scope.severity])
    error_message = "Job severities must be whole numbers from 0 through 4."
  }
}

variable "job_thresholds" {
  description = "Reviewed Container Apps Job failure thresholds."
  type = object({
    failed_executions = optional(number, 1)
  })
  default = {}

  validation {
    condition     = var.job_thresholds.failed_executions >= 1 && floor(var.job_thresholds.failed_executions) == var.job_thresholds.failed_executions
    error_message = "failed_executions must be a positive whole number."
  }
}

variable "tags" {
  description = "Non-sensitive tags applied to every metric alert."
  type        = map(string)
  default     = {}
}

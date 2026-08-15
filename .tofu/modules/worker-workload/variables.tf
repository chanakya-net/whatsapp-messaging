variable "environment" {
  description = "Environment-owned Container App metadata."
  type = object({
    name                         = string
    container_app_environment_id = string
    resource_group_name          = string
    tags                         = map(string)
  })

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{0,30}[a-z0-9]$", var.environment.name)) && !strcontains(var.environment.name, "--")
    error_message = "environment.name must be a 2-32 character lowercase Container App name."
  }

  validation {
    condition = (
      startswith(var.environment.container_app_environment_id, "/subscriptions/") &&
      strcontains(var.environment.container_app_environment_id, "/providers/Microsoft.App/managedEnvironments/") &&
      trimspace(var.environment.resource_group_name) != ""
    )
    error_message = "environment must identify an existing Container Apps environment and resource group."
  }

  validation {
    condition = alltrue([
      for key in ["project", "environment", "location", "repository", "managed_by"] :
      try(trimspace(var.environment.tags[key]), "") != ""
    ])
    error_message = "environment.tags must include all mandatory non-empty tags."
  }

  validation {
    condition = alltrue([
      for key in keys(var.environment.tags) :
      length(regexall("(?i)(secret|token|password|credential|connection)", key)) == 0
    ])
    error_message = "Environment tag keys must not describe secret-bearing values."
  }
}

variable "runtime_identity" {
  description = "Environment runtime identity attached to the worker and used for Key Vault access."
  type = object({
    resource_id  = string
    principal_id = string
    client_id    = string
  })

  validation {
    condition = (
      startswith(var.runtime_identity.resource_id, "/subscriptions/") &&
      strcontains(var.runtime_identity.resource_id, "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/") &&
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.runtime_identity.principal_id)) &&
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.runtime_identity.client_id))
    )
    error_message = "runtime_identity must contain a user-assigned identity resource ID and UUID principal/client IDs."
  }
}

variable "database" {
  description = "Non-secret PostgreSQL connection metadata for the environment database."
  type = object({
    host          = string
    port          = number
    name          = string
    username      = string
    max_pool_size = number
  })

  validation {
    condition = (
      can(regex("^[a-z0-9][a-z0-9.-]+[a-z0-9]$", var.database.host)) &&
      var.database.port >= 1 && var.database.port <= 65535 &&
      can(regex("^[a-z][a-z0-9_]{0,62}$", var.database.name)) &&
      trimspace(var.database.username) != "" &&
      var.database.max_pool_size >= 1
    )
    error_message = "database must contain a valid host, port, name, username, and positive pool size."
  }
}

variable "vault_references" {
  description = "Versionless RabbitMQ and New Relic Key Vault references."
  type = object({
    rabbitmq = object({
      name                = string
      identity            = string
      key_vault_secret_id = string
    })
    new_relic = object({
      name                = string
      identity            = string
      key_vault_secret_id = string
    })
  })

  validation {
    condition = (
      var.vault_references.rabbitmq.name == "rabbitmq-connection-string" &&
      var.vault_references.new_relic.name == "new-relic-otlp-headers"
    )
    error_message = "vault_references must select only the RabbitMQ and New Relic runtime secrets."
  }
}

variable "image" {
  description = "Immutable public worker or bootstrap image coordinates."
  type = object({
    repository = string
    digest     = string
  })

  validation {
    condition = (
      can(regex("^[a-z0-9.-]+(?::[0-9]+)?(?:/[a-z0-9._-]+)+$", var.image.repository)) &&
      !strcontains(var.image.repository, "@") &&
      can(regex("^[0-9a-f]{64}$", var.image.digest))
    )
    error_message = "image must contain an untagged repository and a lowercase 64-character sha256 digest."
  }
}

variable "runtime_configuration" {
  description = "Non-secret MessageBridge worker runtime settings."
  type = object({
    aspnetcore_environment  = string
    topology_prefix         = string
    topology_durable        = bool
    immediate_retry_count   = number
    delayed_redelivery      = list(string)
    recovery_enabled        = bool
    stale_threshold_minutes = number
    cleanup_enabled         = bool
    cleanup_batch_size      = number
    cleanup_interval_ms     = number
    retention_hours         = number
    allowed_tenant_ids      = set(string)
    whatsapp_rate_limit     = number
    email_rate_limit        = number
    rate_limit_window_secs  = number
    otlp_endpoint           = string
    otlp_service_name       = string
  })

  validation {
    condition = (
      contains(["Development", "Production"], var.runtime_configuration.aspnetcore_environment) &&
      can(regex("^[a-z][a-z0-9-]{0,19}$", var.runtime_configuration.topology_prefix)) &&
      var.runtime_configuration.immediate_retry_count >= 0 &&
      var.runtime_configuration.delayed_redelivery == tolist(["00:05:00", "00:15:00", "01:00:00"])
    )
    error_message = "runtime_configuration must use an approved environment, topology prefix, and retry policy."
  }

  validation {
    condition = (
      var.runtime_configuration.stale_threshold_minutes >= 1 &&
      var.runtime_configuration.cleanup_batch_size >= 1 &&
      var.runtime_configuration.cleanup_interval_ms >= 1 &&
      var.runtime_configuration.retention_hours >= 1
    )
    error_message = "Processing-history thresholds, batches, intervals, and retention must be positive."
  }

  validation {
    condition = alltrue([
      for tenant_id in var.runtime_configuration.allowed_tenant_ids :
      trimspace(tenant_id) != "" && !strcontains(tenant_id, ",")
    ])
    error_message = "Tenant IDs must be non-empty and cannot contain commas."
  }

  validation {
    condition = (
      var.runtime_configuration.whatsapp_rate_limit >= 1 &&
      var.runtime_configuration.email_rate_limit >= 1 &&
      var.runtime_configuration.rate_limit_window_secs >= 1
    )
    error_message = "Rate limits and their window must be positive."
  }

  validation {
    condition = (
      can(regex("^https?://[^[:space:]]+$", var.runtime_configuration.otlp_endpoint)) &&
      trimspace(var.runtime_configuration.otlp_service_name) != ""
    )
    error_message = "OTLP endpoint must be an absolute HTTP(S) URL and service name must be non-empty."
  }
}

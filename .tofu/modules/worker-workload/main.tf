resource "azurerm_container_app" "worker" {
  name                         = var.environment.name
  container_app_environment_id = var.environment.container_app_environment_id
  resource_group_name          = var.environment.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.environment.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.runtime_identity.resource_id]
  }

  dynamic "secret" {
    for_each = local.vault_references

    content {
      name                = secret.value.name
      identity            = secret.value.identity
      key_vault_secret_id = secret.value.key_vault_secret_id
    }
  }

  ingress {
    external_enabled           = false
    allow_insecure_connections = false
    target_port                = 8080
    transport                  = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "worker"
      image  = "${var.image.repository}@sha256:${var.image.digest}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = local.non_secret_environment

        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.secret_environment

        content {
          name        = env.key
          secret_name = env.value
        }
      }

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        initial_delay           = 0
        interval_seconds        = 5
        timeout                 = 3
        failure_count_threshold = 6
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        initial_delay           = 30
        interval_seconds        = 10
        timeout                 = 3
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        initial_delay           = 10
        interval_seconds        = 5
        timeout                 = 3
        failure_count_threshold = 1
        success_count_threshold = 1
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition = alltrue([
        for reference in values(local.vault_references) :
        reference.identity == var.runtime_identity.resource_id
      ])
      error_message = "Every Key Vault reference must use the attached runtime identity."
    }

    precondition {
      condition = alltrue([
        for reference in values(local.vault_references) :
        can(regex(
          "^https://[a-z0-9-]+\\.vault\\.azure\\.net/secrets/${reference.name}$",
          reference.key_vault_secret_id,
        ))
      ])
      error_message = "Key Vault references must be versionless URIs matching their approved secret names."
    }
  }
}

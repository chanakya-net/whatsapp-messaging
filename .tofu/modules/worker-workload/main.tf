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

# The migration job is deliberately independent of azurerm_container_app.worker: it is triggered
# manually before deployment, never during worker startup, and a failed execution must leave the
# current worker revision untouched.
resource "azurerm_container_app_job" "migration" {
  name                         = var.migration_job_name
  container_app_environment_id = var.environment.container_app_environment_id
  resource_group_name          = var.environment.resource_group_name
  location                     = var.environment.location
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 1800
  replica_retry_limit          = 0
  tags                         = var.environment.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.migrator_identity.resource_id]
  }

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  template {
    container {
      name   = "migration"
      image  = "${var.migration_image.repository}@sha256:${var.migration_image.digest}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = local.migration_environment

        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition = (
        var.migrator_identity.resource_id != var.runtime_identity.resource_id &&
        var.migrator_identity.principal_id != var.runtime_identity.principal_id &&
        var.migrator_identity.client_id != var.runtime_identity.client_id
      )
      error_message = "The migration job must use a dedicated migrator identity, never the worker runtime identity."
    }
  }
}

# Smoke job: calls worker health endpoints through internal routing.
# Uses pinned public curl image, receives no secrets, and fails on every non-2xx response.
# Manually triggered before deployment to verify worker is live and ready.
resource "azurerm_container_app_job" "smoke" {
  name                         = var.smoke_job_name
  container_app_environment_id = var.environment.container_app_environment_id
  resource_group_name          = var.environment.resource_group_name
  location                      = var.environment.location
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 60
  replica_retry_limit          = 0
  tags                         = var.environment.tags


  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  template {
    container {
      name   = "smoke"
      image  = "curlimages/curl@sha256:3e8d02f5449f6c9f1e42e38ca1dca4db8bb37bf0d1ebb94e39bb48e10f8bd9a2"
      cpu    = 0.25
      memory = "0.5Gi"

      args = [
        "/bin/sh",
        "-c",
        <<-EOC
          set -e
          worker_fqdn="${var.worker_fqdn}"

          echo "Testing /health/live..."
          if ! curl -sS "http://$${worker_fqdn}:8080/health/live" -f -w "\nStatus: %%{http_code}\n"; then
            echo "health/live check failed"
            exit 1
          fi

          echo "Testing /health/ready..."
          if ! curl -sS "http://$${worker_fqdn}:8080/health/ready" -f -w "\nStatus: %%{http_code}\n"; then
            echo "health/ready check failed"
            exit 1
          fi

          echo "Smoke tests passed."
        EOC
      ]
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition = var.smoke_job_name != ""
      error_message = "The smoke job name must not be empty."
    }

    precondition {
      condition = var.worker_fqdn != ""
      error_message = "The worker FQDN must not be empty."
    }
  }
}

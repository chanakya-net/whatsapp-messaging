mock_provider "azurerm" {
  mock_resource "azurerm_container_app" {
    defaults = {
      id                   = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/containerApps/ca-messagebridge-prod-cin-042"
      latest_revision_fqdn = "ca-messagebridge-prod-cin-042.internal.example"
      latest_revision_name = "ca-messagebridge-prod-cin-042--revision"
      outbound_ip_addresses = [
        "20.192.0.20",
        "20.192.0.21",
      ]
    }
  }
}

variables {
  environment = {
    name                         = "ca-messagebridge-prod-cin-042"
    container_app_environment_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-prod-cin-042"
    resource_group_name          = "rg-messagebridge-prod-centralindia-042"
    tags = {
      project     = "messagebridge"
      environment = "prod"
      location    = "centralindia"
      repository  = "chanakya-net/whatsapp-messaging"
      managed_by  = "opentofu"
    }
  }
  runtime_identity = {
    resource_id  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000003"
    client_id    = "00000000-0000-4000-8000-000000000013"
  }
  database = {
    host          = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
    port          = 5432
    name          = "messagebridge_prod"
    username      = "id-messagebridge-runtime-prod-cin-042"
    max_pool_size = 12
  }
  vault_references = {
    rabbitmq = {
      name                = "rabbitmq-connection-string"
      identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
      key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/rabbitmq-connection-string"
    }
    new_relic = {
      name                = "new-relic-otlp-headers"
      identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
      key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/new-relic-otlp-headers"
    }
  }
  image = {
    repository = "ghcr.io/tarampampam/error-pages"
    digest     = "f23f8042a2804669315fd232281d0ccecf1959332314a46e02ca2482064064a6"
  }
  runtime_configuration = {
    aspnetcore_environment  = "Production"
    topology_prefix         = "prod"
    topology_durable        = true
    immediate_retry_count   = 3
    delayed_redelivery      = ["00:05:00", "00:15:00", "01:00:00"]
    recovery_enabled        = true
    stale_threshold_minutes = 30
    cleanup_enabled         = true
    cleanup_batch_size      = 500
    cleanup_interval_ms     = 1000
    retention_hours         = 168
    allowed_tenant_ids      = ["tenant-b", "tenant-a"]
    whatsapp_rate_limit     = 60
    email_rate_limit        = 60
    rate_limit_window_secs  = 60
    otlp_endpoint           = "https://otlp.nr-data.net:4318"
    otlp_service_name       = "MessageBridge.Worker"
  }
}

run "creates_one_private_fixed_worker" {
  command = plan

  assert {
    condition = (
      length(azurerm_container_app.worker[*]) == 1 &&
      azurerm_container_app.worker.name == var.environment.name &&
      azurerm_container_app.worker.container_app_environment_id == var.environment.container_app_environment_id &&
      azurerm_container_app.worker.resource_group_name == var.environment.resource_group_name &&
      azurerm_container_app.worker.revision_mode == "Single" &&
      azurerm_container_app.worker.workload_profile_name == "Consumption"
    )
    error_message = "Exactly one deterministic single-revision Consumption worker must be created."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.ingress) == 1 &&
      azurerm_container_app.worker.ingress[0].external_enabled == false &&
      azurerm_container_app.worker.ingress[0].allow_insecure_connections == false &&
      azurerm_container_app.worker.ingress[0].target_port == 8080 &&
      length(azurerm_container_app.worker.ingress[0].traffic_weight) == 1 &&
      azurerm_container_app.worker.ingress[0].traffic_weight[0].latest_revision == true &&
      azurerm_container_app.worker.ingress[0].traffic_weight[0].percentage == 100
    )
    error_message = "Worker ingress must stay internal, TLS-only, on port 8080 with all traffic on the latest revision."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.identity) == 1 &&
      azurerm_container_app.worker.identity[0].type == "UserAssigned" &&
      azurerm_container_app.worker.identity[0].identity_ids == toset([var.runtime_identity.resource_id])
    )
    error_message = "Worker must attach only its environment runtime identity."
  }

  assert {
    condition = (
      azurerm_container_app.worker.template[0].min_replicas == 1 &&
      azurerm_container_app.worker.template[0].max_replicas == 1 &&
      length(azurerm_container_app.worker.template[0].container) == 1 &&
      azurerm_container_app.worker.template[0].container[0].name == "worker" &&
      azurerm_container_app.worker.template[0].container[0].cpu == 0.5 &&
      azurerm_container_app.worker.template[0].container[0].memory == "1Gi" &&
      azurerm_container_app.worker.template[0].container[0].image == "${var.image.repository}@sha256:${var.image.digest}"
    )
    error_message = "Worker must run one digest-pinned 0.5 CPU/1 GiB container at exactly one replica."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.dapr) == 0 &&
      length(azurerm_container_app.worker.registry) == 0 &&
      length(azurerm_container_app.worker.template[0].init_container) == 0 &&
      length(azurerm_container_app.worker.template[0].azure_queue_scale_rule) == 0 &&
      length(azurerm_container_app.worker.template[0].custom_scale_rule) == 0 &&
      length(azurerm_container_app.worker.template[0].http_scale_rule) == 0 &&
      length(azurerm_container_app.worker.template[0].tcp_scale_rule) == 0
    )
    error_message = "Worker must not add Dapr, registry credentials, init containers, or scale rules."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.template[0].container[0].startup_probe) == 1 &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].path == "/health/live" &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].port == 8080 &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].transport == "HTTP" &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].initial_delay == 0 &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].interval_seconds == 5 &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].timeout == 3 &&
      azurerm_container_app.worker.template[0].container[0].startup_probe[0].failure_count_threshold == 6
    )
    error_message = "Startup probe must allow the approved 30-second live-endpoint startup period."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.template[0].container[0].liveness_probe) == 1 &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].path == "/health/live" &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].port == 8080 &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].transport == "HTTP" &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].initial_delay == 30 &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].interval_seconds == 10 &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].timeout == 3 &&
      azurerm_container_app.worker.template[0].container[0].liveness_probe[0].failure_count_threshold == 3
    )
    error_message = "Liveness probe must use the approved dependency-free endpoint and timings."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.template[0].container[0].readiness_probe) == 1 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].path == "/health/ready" &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].port == 8080 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].transport == "HTTP" &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].initial_delay == 10 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].interval_seconds == 5 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].timeout == 3 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].failure_count_threshold == 1 &&
      azurerm_container_app.worker.template[0].container[0].readiness_probe[0].success_count_threshold == 1
    )
    error_message = "Readiness probe must use the approved dependency endpoint and timings."
  }

  assert {
    condition = (
      output.worker_id == azurerm_container_app.worker.id &&
      output.worker_name == var.environment.name &&
      output.internal_fqdn == "ca-messagebridge-prod-cin-042.internal.example" &&
      output.latest_revision_name == "ca-messagebridge-prod-cin-042--revision" &&
      output.outbound_ip_addresses == toset(["20.192.0.20", "20.192.0.21"]) &&
      output.alertable_resource_ids == toset([azurerm_container_app.worker.id])
    )
    error_message = "Module outputs must expose only safe worker metadata needed by delivery, firewall, and alerts."
  }
}

run "mutable_or_malformed_image_stops" {
  command = plan

  variables {
    image = {
      repository = "ghcr.io/tarampampam/error-pages:latest"
      digest     = "not-a-digest"
    }
  }

  expect_failures = [var.image]
}

mock_provider "azurerm" {}

variables {
  environment = {
    name                         = "ca-messagebridge-prod-cin-042"
    container_app_environment_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-prod-cin-042"
    resource_group_name          = "rg-messagebridge-prod-centralindia-042"
    location                     = "centralindia"
    tags = {
      project     = "messagebridge"
      environment = "prod"
      location    = "centralindia"
      repository  = "chanakya-net/whatsapp-messaging"
      managed_by  = "opentofu"
      release     = "bootstrap"
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
    digest     = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }
  migration_job_name = "mig-messagebridge-prod-cin-042"
  migrator_identity = {
    resource_id  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-migrator-prod-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000005"
    client_id    = "00000000-0000-4000-8000-000000000015"
  }
  migration_database = {
    host          = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
    port          = 5432
    name          = "messagebridge_prod"
    username      = "id-messagebridge-migrator-prod-cin-042"
    max_pool_size = 2
  }
  migration_image = {
    repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
    digest     = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
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
    allowed_tenant_ids      = []
    whatsapp_rate_limit     = 60
    email_rate_limit        = 60
    rate_limit_window_secs  = 60
    otlp_endpoint           = "https://otlp.nr-data.net:4318"
    otlp_service_name       = "MessageBridge.Worker"
  }
}

run "apply_bootstrap_image" {
  command = apply

  assert {
    condition     = azurerm_container_app.worker.template[0].container[0].image == "ghcr.io/tarampampam/error-pages@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    error_message = "Test setup must apply the original bootstrap digest."
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "ghcr.io/chanakya-net/whatsapp-messaging/migrate@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
    error_message = "Test setup must apply the original migration digest."
  }
}

run "ignores_only_delivery_image_changes" {
  command = plan

  variables {
    environment = {
      name                         = "ca-messagebridge-prod-cin-042"
      container_app_environment_id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-prod-cin-042"
      resource_group_name          = "rg-messagebridge-prod-centralindia-042"
      location                     = "centralindia"
      tags = {
        project     = "messagebridge"
        environment = "prod"
        location    = "centralindia"
        repository  = "chanakya-net/whatsapp-messaging"
        managed_by  = "opentofu"
        release     = "candidate"
      }
    }
    image = {
      repository = "ghcr.io/tarampampam/error-pages"
      digest     = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    }
    migration_image = {
      repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
      digest     = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    }
    migration_database = {
      host          = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
      port          = 5432
      name          = "messagebridge_prod"
      username      = "id-messagebridge-migrator-prod-cin-042"
      max_pool_size = 3
    }
  }

  assert {
    condition     = azurerm_container_app.worker.template[0].container[0].image == "ghcr.io/tarampampam/error-pages@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    error_message = "Delivery-owned image drift must retain the applied digest in OpenTofu plans."
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "ghcr.io/chanakya-net/whatsapp-messaging/migrate@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
    error_message = "Delivery-owned migration image drift must retain the applied digest in OpenTofu plans."
  }

  assert {
    condition     = azurerm_container_app.worker.tags["release"] == "candidate"
    error_message = "Non-image drift must remain visible so the lifecycle exception cannot hide configuration changes."
  }

  assert {
    condition = one([
      for setting in azurerm_container_app_job.migration.template[0].container[0].env : setting
      if setting.name == "Database__MaxPoolSize"
    ]).value == "3"
    error_message = "Non-image migration job drift must remain visible so the lifecycle exception stays image-only."
  }
}

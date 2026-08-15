mock_provider "azurerm" {}

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

run "wires_matching_secrets_and_runtime_configuration" {
  command = plan

  assert {
    condition = (
      length(azurerm_container_app.worker.secret) == 2 &&
      toset([for secret in azurerm_container_app.worker.secret : secret.name]) == toset([
        "rabbitmq-connection-string",
        "new-relic-otlp-headers",
      ]) &&
      alltrue([
        for secret in azurerm_container_app.worker.secret :
        secret.identity == var.runtime_identity.resource_id &&
        secret.key_vault_secret_id == var.vault_references[secret.name == "rabbitmq-connection-string" ? "rabbitmq" : "new_relic"].key_vault_secret_id &&
        secret.value == null
      ])
    )
    error_message = "Worker secrets must contain only versionless identity-backed RabbitMQ and New Relic references."
  }

  assert {
    condition = alltrue([
      for name, secret_name in {
        RabbitMq__ConnectionString = "rabbitmq-connection-string"
        OTEL_EXPORTER_OTLP_HEADERS = "new-relic-otlp-headers"
        } : one([
          for setting in azurerm_container_app.worker.template[0].container[0].env : setting
          if setting.name == name
      ]).secret_name == secret_name
    ])
    error_message = "Secret environment variables must refer to Container App secret names, never values."
  }

  assert {
    condition = alltrue([
      for name, value in {
        HTTP_PORT                                                     = "8080"
        Database__Host                                                = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
        Database__Port                                                = "5432"
        Database__Database                                            = "messagebridge_prod"
        Database__Username                                            = "id-messagebridge-runtime-prod-cin-042"
        Database__UseEntraAuth                                        = "true"
        Database__MaxPoolSize                                         = "12"
        Database__ManagedIdentityClientId                             = var.runtime_identity.client_id
        MessageBridge__Topology__EnvironmentPrefix                    = "prod"
        MessageBridge__Topology__Durable                              = "true"
        MessageBridge__TransportRetry__ImmediateRetryCount            = "3"
        MessageBridge__TransportRetry__DelayedRedeliveryIntervals__0  = "00:05:00"
        MessageBridge__TransportRetry__DelayedRedeliveryIntervals__1  = "00:15:00"
        MessageBridge__TransportRetry__DelayedRedeliveryIntervals__2  = "01:00:00"
        MessageBridge__ProcessingHistory__RecoveryEnabled             = "true"
        MessageBridge__ProcessingHistory__StaleThresholdMinutes       = "30"
        MessageBridge__ProcessingHistory__CleanupEnabled              = "true"
        MessageBridge__ProcessingHistory__CleanupBatchSize            = "500"
        MessageBridge__ProcessingHistory__CleanupIntervalMilliseconds = "1000"
        MessageBridge__ProcessingHistory__ProductionRetentionHours    = "168"
        MessageBridge__Tenants__AllowedTenantIds                      = "tenant-a,tenant-b"
        MessageBridge__RateLimiting__WhatsAppPermitsPerWindow         = "60"
        MessageBridge__RateLimiting__EmailPermitsPerWindow            = "60"
        MessageBridge__RateLimiting__WindowSizeSeconds                = "60"
        Observability__OtlpEndpoint                                   = "https://otlp.nr-data.net:4318"
        Observability__ServiceName                                    = "MessageBridge.Worker"
        Observability__MetricsEndpointEnabled                         = "false"
        OTEL_EXPORTER_OTLP_ENDPOINT                                   = "https://otlp.nr-data.net:4318"
        OTEL_SERVICE_NAME                                             = "MessageBridge.Worker"
        ASPNETCORE_ENVIRONMENT                                        = "Production"
        ASPNETCORE_HTTP_PORTS                                         = "8080"
        AZURE_CLIENT_ID                                               = var.runtime_identity.client_id
        } : one([
          for setting in azurerm_container_app.worker.template[0].container[0].env : setting
          if setting.name == name
      ]).value == value
    ])
    error_message = "Worker must receive the complete non-secret database, topology, retry, history, tenant, rate, OTLP, ASP.NET, and identity configuration."
  }

  assert {
    condition = (
      length(azurerm_container_app.worker.template[0].container[0].env) == 34 &&
      !contains([for setting in azurerm_container_app.worker.template[0].container[0].env : setting.name], "MessageBridge__ProcessingHistory__DevelopmentRetentionHours") &&
      !contains([for setting in azurerm_container_app.worker.template[0].container[0].env : setting.name], "ConnectionStrings__DefaultConnection") &&
      !contains([for setting in azurerm_container_app.worker.template[0].container[0].env : setting.name], "MESSAGEBRIDGE_CONNECTION_STRING")
    )
    error_message = "Worker environment must contain only the approved active-environment configuration surface."
  }
}

run "mismatched_vault_identity_stops" {
  command = plan

  variables {
    vault_references = {
      rabbitmq = {
        name                = "rabbitmq-connection-string"
        identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-migrator-prod-cin-042"
        key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/rabbitmq-connection-string"
      }
      new_relic = {
        name                = "new-relic-otlp-headers"
        identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
        key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/new-relic-otlp-headers"
      }
    }
  }

  expect_failures = [azurerm_container_app.worker]
}

run "versioned_vault_reference_stops" {
  command = plan

  variables {
    vault_references = {
      rabbitmq = {
        name                = "rabbitmq-connection-string"
        identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
        key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/rabbitmq-connection-string/00000000000000000000000000000000"
      }
      new_relic = {
        name                = "new-relic-otlp-headers"
        identity            = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
        key_vault_secret_id = "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/new-relic-otlp-headers"
      }
    }
  }

  expect_failures = [azurerm_container_app.worker]
}

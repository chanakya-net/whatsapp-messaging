locals {
  project      = "messagebridge"
  environment  = "dev"
  location     = "centralindia"
  region_token = "cin"

  resource_group_name = "rg-${local.project}-${local.environment}-${local.location}-${var.bootstrap_serial}"

  # Azure Key Vault names allow at most 24 characters, so msgbr is the stable project token.
  vault_name = "kv-msgbr-${local.environment}-${local.region_token}-${var.bootstrap_serial}"

  container_app_environment_name = "cae-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  runtime_identity_name          = "id-${local.project}-runtime-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  migrator_identity_name         = "id-${local.project}-migrator-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  worker_name                    = "ca-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  worker_fqdn                    = "${local.worker_name}.${local.container_app_environment_name}.internal"
  worker_database_host           = "psql-${local.project}-shared-${local.region_token}-${var.bootstrap_serial}.postgres.database.azure.com"
  worker_database_name           = "messagebridge_dev"

  # Container Apps Job names allow at most 32 characters, so mig is the stable migration token.
  migration_job_name = "mig-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  smoke_job_name     = "smoke-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"

  mandatory_tags = merge(var.tags, {
    project     = local.project
    environment = local.environment
    location    = local.location
    repository  = var.repository
    managed_by  = "opentofu"
  })

  worker_vault_references = {
    rabbitmq  = module.key_vault.container_app_secret_references["rabbitmq-connection-string"]
    new_relic = module.key_vault.container_app_secret_references["new-relic-otlp-headers"]
  }

  # The migration job authenticates to the dev database as the dev migrator principal and never
  # reads a Key Vault secret, so it needs no vault reference of its own.
  migrator_identity = {
    resource_id  = azurerm_user_assigned_identity.migrator.id
    principal_id = azurerm_user_assigned_identity.migrator.principal_id
    client_id    = azurerm_user_assigned_identity.migrator.client_id
  }

  migration_database = {
    host          = local.worker_database_host
    port          = 5432
    name          = local.worker_database_name
    username      = azurerm_user_assigned_identity.migrator.name
    max_pool_size = 2
  }

  worker_runtime_configuration = {
    aspnetcore_environment  = "Development"
    topology_prefix         = "dev"
    topology_durable        = true
    immediate_retry_count   = 3
    delayed_redelivery      = ["00:05:00", "00:15:00", "01:00:00"]
    recovery_enabled        = true
    stale_threshold_minutes = 30
    cleanup_enabled         = true
    cleanup_batch_size      = 500
    cleanup_interval_ms     = 1000
    retention_hours         = 24
    allowed_tenant_ids      = var.worker_allowed_tenant_ids
    whatsapp_rate_limit     = 60
    email_rate_limit        = 60
    rate_limit_window_secs  = 60
    otlp_endpoint           = var.worker_otlp_endpoint
    otlp_service_name       = "MessageBridge.Worker"
  }
}

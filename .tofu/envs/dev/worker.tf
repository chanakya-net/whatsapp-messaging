module "worker" {
  source = "../../modules/worker-workload"

  environment = {
    name                         = local.worker_name
    container_app_environment_id = module.container_environment.environment_id
    resource_group_name          = local.resource_group_name
    location                     = local.location
    tags                         = merge(local.mandatory_tags, { component = "worker" })
  }
  runtime_identity = {
    resource_id  = azurerm_user_assigned_identity.runtime.id
    principal_id = azurerm_user_assigned_identity.runtime.principal_id
    client_id    = azurerm_user_assigned_identity.runtime.client_id
  }
  database = {
    host          = local.worker_database_host
    port          = 5432
    name          = local.worker_database_name
    username      = azurerm_user_assigned_identity.runtime.name
    max_pool_size = 12
  }
  vault_references      = local.worker_vault_references
  image                 = var.worker_image
  runtime_configuration = local.worker_runtime_configuration

  # The migration job reads no Key Vault secret, so the module is not made to depend on the vault
  # as a whole. The worker keeps its vault ordering through local.worker_vault_references.
  migration_job_name = local.migration_job_name
  migrator_identity  = local.migrator_identity
  migration_database = local.migration_database
  migration_image    = var.migration_image

  # Smoke job calls worker health endpoints through internal routing with no secrets.
  smoke_job_name = local.smoke_job_name
}

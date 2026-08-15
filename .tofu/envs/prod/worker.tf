module "worker" {
  source = "../../modules/worker-workload"

  environment = {
    name                         = local.worker_name
    container_app_environment_id = module.container_environment.environment_id
    resource_group_name          = local.resource_group_name
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

  depends_on = [module.key_vault]
}

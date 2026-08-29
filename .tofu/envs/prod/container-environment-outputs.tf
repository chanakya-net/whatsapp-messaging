output "container_app_environment_id" {
  description = "Resource ID of the isolated prod Container Apps managed environment."
  value       = module.container_environment.environment_id
}

output "container_app_environment_name" {
  description = "Name of the isolated prod Container Apps managed environment."
  value       = module.container_environment.environment_name
}

output "container_app_environment_default_domain" {
  description = "Default domain of the prod managed environment."
  value       = module.container_environment.default_domain
}

output "container_app_environment_static_ip_address" {
  description = "Static address of the prod managed environment."
  value       = module.container_environment.static_ip_address
}

output "container_app_environment_outbound_ip_addresses" {
  description = "Prod environment-level outbound addresses; later slices union these with workload outputs before firewall reconciliation."
  value       = module.container_environment.outbound_ip_addresses
}

output "runtime_identity" {
  description = "Root-owned prod runtime workload identity metadata."
  value = {
    resource_id  = azurerm_user_assigned_identity.runtime.id
    principal_id = azurerm_user_assigned_identity.runtime.principal_id
    client_id    = azurerm_user_assigned_identity.runtime.client_id
  }
}

output "migrator_identity" {
  description = "Root-owned prod migration workload identity metadata; it holds no vault role."
  value = {
    resource_id  = azurerm_user_assigned_identity.migrator.id
    principal_id = azurerm_user_assigned_identity.migrator.principal_id
    client_id    = azurerm_user_assigned_identity.migrator.client_id
  }
}

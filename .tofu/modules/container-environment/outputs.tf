output "environment_id" {
  description = "Resource ID of the environment-scoped Container Apps managed environment."
  value       = azurerm_container_app_environment.this.id
}

output "environment_name" {
  description = "Name of the environment-scoped Container Apps managed environment."
  value       = azurerm_container_app_environment.this.name
}

output "default_domain" {
  description = "Provider-computed default domain used for in-environment workload addressing."
  value       = azurerm_container_app_environment.this.default_domain
}

output "static_ip_address" {
  description = "Provider-computed static address of the managed environment."
  value       = azurerm_container_app_environment.this.static_ip_address
}

# AzureRM 4.x exposes only the environment static address here; per-workload outbound addresses
# live on the Container App and Container App Job resources created in later slices. Downstream
# firewall reconciliation must union this set with those workload outputs, never treat it as
# the complete egress set on its own.
output "outbound_ip_addresses" {
  description = "Environment-level outbound addresses; union with workload outputs before firewall reconciliation."
  value       = toset(compact([azurerm_container_app_environment.this.static_ip_address]))
}

output "alertable_resource_ids" {
  description = "Resource IDs eligible for downstream environment alerting."
  value       = toset([azurerm_container_app_environment.this.id])
}

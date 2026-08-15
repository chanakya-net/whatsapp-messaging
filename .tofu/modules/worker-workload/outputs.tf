output "worker_id" {
  description = "Resource ID of the environment worker."
  value       = azurerm_container_app.worker.id
}

output "worker_name" {
  description = "Name of the environment worker."
  value       = azurerm_container_app.worker.name
}

output "internal_fqdn" {
  description = "Internal-only FQDN of the worker's latest revision."
  value       = azurerm_container_app.worker.latest_revision_fqdn
}

output "latest_revision_name" {
  description = "Latest revision name used by delivery verification."
  value       = azurerm_container_app.worker.latest_revision_name
}

output "outbound_ip_addresses" {
  description = "Worker outbound addresses for downstream database firewall reconciliation."
  value       = toset(azurerm_container_app.worker.outbound_ip_addresses)
}

output "alertable_resource_ids" {
  description = "Worker IDs eligible for downstream environment alerting."
  value       = toset([azurerm_container_app.worker.id])
}

output "worker_id" {
  description = "Resource ID of the private prod worker."
  value       = module.worker.worker_id
}

output "worker_name" {
  description = "Name of the private prod worker."
  value       = module.worker.worker_name
}

output "worker_internal_fqdn" {
  description = "Internal-only FQDN of the prod worker."
  value       = module.worker.internal_fqdn
}

output "worker_latest_revision_name" {
  description = "Latest prod worker revision for delivery verification."
  value       = module.worker.latest_revision_name
}

output "worker_outbound_ip_addresses" {
  description = "Prod worker egress addresses for downstream database firewall reconciliation."
  value       = module.worker.outbound_ip_addresses
}

output "worker_alertable_resource_ids" {
  description = "Prod worker IDs eligible for downstream environment alerting."
  value       = module.worker.alertable_resource_ids
}

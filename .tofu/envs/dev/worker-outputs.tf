output "worker_id" {
  description = "Resource ID of the private dev worker."
  value       = module.worker.worker_id
}

output "worker_name" {
  description = "Name of the private dev worker."
  value       = module.worker.worker_name
}

output "worker_internal_fqdn" {
  description = "Internal-only FQDN of the dev worker."
  value       = module.worker.internal_fqdn
}

output "worker_latest_revision_name" {
  description = "Latest dev worker revision for delivery verification."
  value       = module.worker.latest_revision_name
}

output "worker_outbound_ip_addresses" {
  description = "Dev worker egress addresses for downstream database firewall reconciliation."
  value       = module.worker.outbound_ip_addresses
}

output "migration_job_id" {
  description = "Resource ID of the manually triggered dev migration job."
  value       = module.worker.migration_job_id
}

output "migration_job_name" {
  description = "Name of the manually triggered dev migration job."
  value       = module.worker.migration_job_name
}

output "migration_job_outbound_ip_addresses" {
  description = "Dev migration job egress addresses for downstream database firewall reconciliation."
  value       = module.worker.migration_job_outbound_ip_addresses
}

output "worker_alertable_resource_ids" {
  description = "Dev worker IDs eligible for downstream environment alerting."
  value       = module.worker.alertable_resource_ids
}

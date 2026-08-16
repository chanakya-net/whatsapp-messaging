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

output "smoke_job_id" {
  description = "Resource ID of the manually triggered dev smoke test job."
  value       = module.worker.smoke_job_id
}

output "smoke_job_name" {
  description = "Name of the manually triggered dev smoke test job."
  value       = module.worker.smoke_job_name
}

output "smoke_job_outbound_ip_addresses" {
  description = "Dev smoke job egress addresses for downstream database firewall reconciliation."
  value       = module.worker.smoke_job_outbound_ip_addresses
}

output "migration_job_outbound_ip_addresses" {
  description = "Dev migration job egress addresses for downstream database firewall reconciliation."
  value       = module.worker.migration_job_outbound_ip_addresses
}

output "reviewed_postgres_egress" {
  description = "Complete reviewed dev PostgreSQL egress, labelled by source and deduplicated."
  value = {
    environment = "dev"
    sources     = local.reviewed_postgres_egress_sources
    ranges = setunion(
      local.reviewed_postgres_egress_sources.container_environment,
      local.reviewed_postgres_egress_sources.worker,
      local.reviewed_postgres_egress_sources.migration,
      local.reviewed_postgres_egress_sources.smoke,
    )
  }
}

output "worker_alertable_resource_ids" {
  description = "Dev worker IDs eligible for downstream environment alerting."
  value       = module.worker.alertable_resource_ids
}

locals {
  reviewed_postgres_egress_sources = {
    container_environment = toset([
      for address in module.container_environment.outbound_ip_addresses : "${address}/32"
    ])
    worker    = module.worker.postgres_egress_ranges.worker
    migration = module.worker.postgres_egress_ranges.migration
    smoke     = module.worker.postgres_egress_ranges.smoke
  }
}

output "server_id" {
  description = "Resource ID of the shared PostgreSQL server."
  value       = module.database.server_id
}

output "server_name" {
  description = "Name of the shared PostgreSQL server."
  value       = module.database.server_name
}

output "server_fqdn" {
  description = "TLS endpoint for the shared PostgreSQL server."
  value       = module.database.server_fqdn
}

output "database_names" {
  description = "Development and production database names."
  value       = module.database.database_names
}

output "alertable_resource_ids" {
  description = "Shared resource IDs eligible for platform alerting."
  value       = module.database.alertable_resource_ids
}

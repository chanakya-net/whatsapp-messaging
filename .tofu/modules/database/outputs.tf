output "server_id" {
  description = "Resource ID of the protected PostgreSQL server."
  value       = azurerm_postgresql_flexible_server.this.id
}

output "server_name" {
  description = "Name of the PostgreSQL server."
  value       = azurerm_postgresql_flexible_server.this.name
}

output "server_fqdn" {
  description = "TLS endpoint used by downstream environments."
  value       = azurerm_postgresql_flexible_server.this.fqdn
}

output "database_names" {
  description = "Names of application databases on the shared server."
  value       = toset([for database in azurerm_postgresql_flexible_server_database.this : database.name])
}

output "alertable_resource_ids" {
  description = "Resource IDs eligible for downstream platform alerting."
  value       = toset([azurerm_postgresql_flexible_server.this.id])
}

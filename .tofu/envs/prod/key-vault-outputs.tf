output "vault_id" {
  description = "Resource ID of the protected prod vault."
  value       = module.key_vault.vault_id
}

output "vault_name" {
  description = "Name of the protected prod vault."
  value       = module.key_vault.vault_name
}

output "vault_uri" {
  description = "Base URI of the protected prod vault."
  value       = module.key_vault.vault_uri
}

output "alertable_resource_ids" {
  description = "Prod vault IDs eligible for downstream platform alerting."
  value       = module.key_vault.alertable_resource_ids
}

output "container_app_secret_references" {
  description = "Versionless prod Key Vault references for later Container Apps wiring."
  value       = module.key_vault.container_app_secret_references
}

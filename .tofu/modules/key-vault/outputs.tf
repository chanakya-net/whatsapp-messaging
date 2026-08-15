output "vault_id" {
  description = "Resource ID of the protected environment vault."
  value       = azurerm_key_vault.this.id
}

output "vault_name" {
  description = "Name of the protected environment vault."
  value       = azurerm_key_vault.this.name
}

output "vault_uri" {
  description = "Base URI of the protected environment vault."
  value       = azurerm_key_vault.this.vault_uri
}

output "alertable_resource_ids" {
  description = "Resource IDs eligible for downstream platform alerting."
  value       = toset([azurerm_key_vault.this.id])
}

output "container_app_secret_references" {
  description = "Versionless Key Vault references for later Container Apps wiring."
  value = {
    for name in local.approved_secret_names : name => {
      name                = name
      identity            = var.runtime_identity.resource_id
      key_vault_secret_id = "${azurerm_key_vault.this.vault_uri}secrets/${name}"
    }
  }
}

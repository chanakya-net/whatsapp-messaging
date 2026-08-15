mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-dev-cin-042"
      vault_uri = "https://kv-msgbr-dev-cin-042.vault.azure.net/"
    }
  }
}

variables {
  name                = "kv-msgbr-dev-cin-042"
  resource_group_name = "rg-messagebridge-dev-centralindia-042"
  location            = "centralindia"
  tenant_id           = "00000000-0000-4000-8000-000000000001"
  runtime_identity = {
    principal_id = "00000000-0000-4000-8000-000000000003"
    resource_id  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-worker-dev-cin-042"
  }
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
  tags = {
    project     = "messagebridge"
    environment = "dev"
  }
}

run "vault_uses_protected_rbac_posture" {
  command = plan

  assert {
    condition     = azurerm_key_vault.this.sku_name == "standard"
    error_message = "The vault must use the Standard SKU."
  }

  assert {
    condition     = azurerm_key_vault.this.rbac_authorization_enabled == true && azurerm_key_vault.this.purge_protection_enabled == true
    error_message = "The vault must enable RBAC authorization and purge protection."
  }

  assert {
    condition     = azurerm_key_vault.this.soft_delete_retention_days == 90
    error_message = "The vault must retain soft-deleted objects for 90 days."
  }

  assert {
    condition     = length(azurerm_key_vault.this.access_policy) == 0
    error_message = "Legacy access policies must remain absent."
  }
}

run "outputs_only_versionless_reference_metadata" {
  command = plan

  assert {
    condition = toset(keys(output.container_app_secret_references)) == toset([
      "rabbitmq-connection-string",
      "new-relic-otlp-headers",
      "whatsapp-provider-placeholder",
      "email-provider-placeholder",
    ])
    error_message = "The output must expose exactly the four approved secret references."
  }

  assert {
    condition = alltrue([
      for name, reference in output.container_app_secret_references :
      reference.name == name &&
      reference.identity == var.runtime_identity.resource_id &&
      reference.key_vault_secret_id == "https://kv-msgbr-dev-cin-042.vault.azure.net/secrets/${name}"
    ])
    error_message = "Every reference must use the runtime identity and a versionless URI."
  }

  assert {
    condition     = output.vault_id == azurerm_key_vault.this.id && output.vault_name == var.name && output.vault_uri == "https://kv-msgbr-dev-cin-042.vault.azure.net/"
    error_message = "Vault outputs must contain safe resource metadata only."
  }
}

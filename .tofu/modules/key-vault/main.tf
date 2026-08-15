locals {
  approved_secret_names = toset([
    "rabbitmq-connection-string",
    "new-relic-otlp-headers",
    "whatsapp-provider-placeholder",
    "email-provider-placeholder",
  ])

  vault_access = {
    runtime = {
      principal_id                     = var.runtime_identity.principal_id
      principal_type                   = "ServicePrincipal"
      role_definition_name             = "Key Vault Secrets User"
      skip_service_principal_aad_check = true
    }
    operator = {
      principal_id                     = var.operator_identity.principal_id
      principal_type                   = var.operator_identity.principal_type
      role_definition_name             = "Key Vault Secrets Officer"
      skip_service_principal_aad_check = var.operator_identity.principal_type == "ServicePrincipal"
    }
  }
}

resource "azurerm_key_vault" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = var.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90

  tags = var.tags

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_role_assignment" "vault_access" {
  for_each = local.vault_access

  name                             = uuidv5("dns", "${var.name}/${each.key}")
  scope                            = azurerm_key_vault.this.id
  role_definition_name             = each.value.role_definition_name
  principal_id                     = each.value.principal_id
  principal_type                   = each.value.principal_type
  skip_service_principal_aad_check = each.value.skip_service_principal_aad_check
}

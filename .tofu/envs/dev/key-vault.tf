module "key_vault" {
  source = "../../modules/key-vault"

  name                = local.vault_name
  resource_group_name = local.resource_group_name
  location            = local.location
  tenant_id           = var.tenant_id
  runtime_identity = {
    principal_id = azurerm_user_assigned_identity.runtime.principal_id
    resource_id  = azurerm_user_assigned_identity.runtime.id
  }
  operator_identity = var.operator_identity
  tags              = local.mandatory_tags
}

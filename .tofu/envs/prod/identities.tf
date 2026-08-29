# Runtime and migrator workload identities are owned by this environment root and never shared
# with another environment. Neither identity receives a role here beyond the same-environment
# Key Vault reader granted by the vault module; database grants belong to later slices.
resource "azurerm_user_assigned_identity" "runtime" {
  name                = local.runtime_identity_name
  resource_group_name = local.resource_group_name
  location            = local.location

  tags = merge(local.mandatory_tags, { purpose = "runtime" })
}

resource "azurerm_user_assigned_identity" "migrator" {
  name                = local.migrator_identity_name
  resource_group_name = local.resource_group_name
  location            = local.location

  tags = merge(local.mandatory_tags, { purpose = "migrator" })
}

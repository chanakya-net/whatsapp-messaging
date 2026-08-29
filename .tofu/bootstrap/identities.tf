resource "azurerm_user_assigned_identity" "workflow" {
  for_each = local.workflow_identities

  name                = "id-${local.project}-${each.key}-${local.region_token}-${var.bootstrap_serial}"
  resource_group_name = azurerm_resource_group.ownership["bootstrap"].name
  location            = azurerm_resource_group.ownership["bootstrap"].location
  tags = merge(var.tags, local.mandatory_tags, {
    environment = each.value.environment
    purpose     = "github-oidc-${each.key}"
  })
}

resource "azurerm_federated_identity_credential" "workflow" {
  for_each = local.workflow_identities

  name                      = each.key == "plan" ? "github-pull-request-plan" : "github-environment-${each.key}"
  user_assigned_identity_id = azurerm_user_assigned_identity.workflow[each.key].id
  issuer                    = "https://token.actions.githubusercontent.com"
  audience                  = ["api://AzureADTokenExchange"]
  subject                   = each.value.subject
}

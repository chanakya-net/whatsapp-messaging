locals {
  role_definitions = {
    plan = {
      description = "Read-only plan access to MessageBridge environment resources."
      actions     = ["*/read"]
    }
    shared = {
      description = "Manage MessageBridge shared network, observability, registry, identity, and vault control planes."
      actions = [
        "Microsoft.Authorization/roleAssignments/delete",
        "Microsoft.Authorization/roleAssignments/read",
        "Microsoft.Authorization/roleAssignments/write",
        "Microsoft.ContainerRegistry/registries/*",
        "Microsoft.Insights/diagnosticSettings/*",
        "Microsoft.KeyVault/vaults/*",
        "Microsoft.ManagedIdentity/userAssignedIdentities/*",
        "Microsoft.Network/*/read",
        "Microsoft.Network/privateDnsZones/*",
        "Microsoft.Network/privateEndpoints/*",
        "Microsoft.Network/virtualNetworks/*",
        "Microsoft.OperationalInsights/workspaces/*",
        "Microsoft.Resources/deployments/*",
        "Microsoft.Resources/subscriptions/resourceGroups/read",
      ]
    }
    environment = {
      description = "Manage MessageBridge application environment control planes without secret data access."
      actions = [
        "Microsoft.App/containerApps/*",
        "Microsoft.App/jobs/*",
        "Microsoft.App/managedEnvironments/*",
        "Microsoft.Authorization/roleAssignments/delete",
        "Microsoft.Authorization/roleAssignments/read",
        "Microsoft.Authorization/roleAssignments/write",
        "Microsoft.DBforPostgreSQL/flexibleServers/*",
        "Microsoft.Insights/components/*",
        "Microsoft.Insights/diagnosticSettings/*",
        "Microsoft.KeyVault/vaults/*",
        "Microsoft.ManagedIdentity/userAssignedIdentities/*",
        "Microsoft.Network/privateDnsZones/read",
        "Microsoft.Network/privateEndpoints/*",
        "Microsoft.Network/virtualNetworks/read",
        "Microsoft.Network/virtualNetworks/subnets/join/action",
        "Microsoft.OperationalInsights/workspaces/read",
        "Microsoft.Resources/deployments/*",
        "Microsoft.Resources/subscriptions/resourceGroups/read",
      ]
    }
  }
}

resource "azurerm_role_definition" "operations" {
  for_each = local.role_definitions

  name               = "MessageBridge ${title(each.key)} Operations ${var.bootstrap_serial}"
  role_definition_id = local.custom_role_definition_ids[each.key]
  scope              = local.subscription_scope
  description        = each.value.description

  permissions {
    actions          = each.value.actions
    not_actions      = []
    data_actions     = []
    not_data_actions = []
  }

  assignable_scopes = [local.subscription_scope]
}

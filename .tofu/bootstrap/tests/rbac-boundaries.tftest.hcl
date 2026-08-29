mock_provider "azurerm" {
  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-bootstrap-centralindia-042/providers/Microsoft.Storage/storageAccounts/messagebridgetfstate042"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-bootstrap-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock"
      principal_id = "00000000-0000-4000-8000-000000000003"
    }
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
}

run "roles_contain_only_required_resource_families" {
  command = plan

  assert {
    condition     = toset(keys(azurerm_role_definition.operations)) == toset(["plan", "shared", "environment"])
    error_message = "Planning, shared, and environment operations require distinct custom roles."
  }

  assert {
    condition     = toset(azurerm_role_definition.operations["plan"].permissions[0].actions) == toset(["*/read"]) && length(azurerm_role_definition.operations["plan"].permissions[0].data_actions) == 0
    error_message = "Planning role must be control-plane read-only."
  }

  assert {
    condition = alltrue([
      contains(azurerm_role_definition.operations["shared"].permissions[0].actions, "Microsoft.ContainerRegistry/registries/*"),
      contains(azurerm_role_definition.operations["shared"].permissions[0].actions, "Microsoft.Network/*/read"),
      contains(azurerm_role_definition.operations["environment"].permissions[0].actions, "Microsoft.App/containerApps/*"),
      contains(azurerm_role_definition.operations["environment"].permissions[0].actions, "Microsoft.DBforPostgreSQL/flexibleServers/*")
    ])
    error_message = "Apply roles must cover only their downstream shared or environment resource families."
  }

  assert {
    condition     = alltrue([for role in values(azurerm_role_definition.operations) : length([for action in concat(tolist(role.permissions[0].actions), tolist(role.permissions[0].data_actions)) : action if strcontains(lower(action), "vaults/secrets")]) == 0])
    error_message = "Custom roles must never grant Key Vault secret operations."
  }
}

run "assignments_are_environment_scoped" {
  command = plan

  assert {
    condition = toset(keys(azurerm_role_assignment.workflow)) == toset([
      "plan-shared-control", "plan-dev-control", "plan-prod-control",
      "plan-shared-state", "plan-dev-state", "plan-prod-state",
      "shared-control", "shared-state", "dev-control", "dev-state",
      "prod-control", "prod-state"
    ])
    error_message = "Only the explicitly approved principal/scope pairs may be assigned."
  }

  assert {
    condition     = alltrue([for assignment in values(azurerm_role_assignment.workflow) : assignment.scope != "/subscriptions/00000000-0000-4000-8000-000000000002"])
    error_message = "Workflow identities must never receive subscription-scoped assignments."
  }

  assert {
    condition = alltrue([
      for environment in ["shared", "dev", "prod"] :
      azurerm_role_assignment.workflow["${environment}-control"].scope == "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-${environment}-centralindia-042"
    ])
    error_message = "Mutating identities must be assigned only to their matching resource group."
  }

  assert {
    condition     = length([for assignment in values(azurerm_role_assignment.workflow) : assignment.scope if strcontains(lower(assignment.scope), "/containers/bootstrap")]) == 0
    error_message = "No workflow identity may access bootstrap state."
  }
}

run "cross_environment_pairs_are_denied" {
  command = plan

  assert {
    condition = alltrue([
      for forbidden in [
        "shared-dev-control", "shared-prod-control", "dev-shared-control",
        "dev-prod-control", "prod-shared-control", "prod-dev-control",
        "shared-dev-state", "shared-prod-state", "dev-shared-state",
        "dev-prod-state", "prod-shared-state", "prod-dev-state"
      ] : !contains(keys(azurerm_role_assignment.workflow), forbidden)
    ])
    error_message = "Cross-environment control-plane and state assignments are forbidden."
  }
}

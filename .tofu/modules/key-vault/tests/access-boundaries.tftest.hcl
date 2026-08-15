mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-prod-cin-042"
      vault_uri = "https://kv-msgbr-prod-cin-042.vault.azure.net/"
    }
  }
}

variables {
  name                = "kv-msgbr-prod-cin-042"
  resource_group_name = "rg-messagebridge-prod-centralindia-042"
  location            = "centralindia"
  tenant_id           = "00000000-0000-4000-8000-000000000001"
  runtime_identity = {
    principal_id = "00000000-0000-4000-8000-000000000003"
    resource_id  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-worker-prod-cin-042"
  }
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
}

run "grants_only_runtime_read_and_operator_management" {
  command = plan

  assert {
    condition     = toset(keys(azurerm_role_assignment.vault_access)) == toset(["runtime", "operator"])
    error_message = "Exactly runtime and operator vault assignments must exist."
  }

  assert {
    condition = alltrue([
      for assignment in values(azurerm_role_assignment.vault_access) :
      assignment.scope == azurerm_key_vault.this.id
    ])
    error_message = "Data-plane assignments must be scoped to this vault."
  }

  assert {
    condition = (
      azurerm_role_assignment.vault_access["runtime"].role_definition_name == "Key Vault Secrets User" &&
      azurerm_role_assignment.vault_access["runtime"].principal_id == var.runtime_identity.principal_id &&
      azurerm_role_assignment.vault_access["runtime"].principal_type == "ServicePrincipal" &&
      azurerm_role_assignment.vault_access["runtime"].skip_service_principal_aad_check == true
    )
    error_message = "Only the runtime identity may receive secret read access."
  }

  assert {
    condition = (
      azurerm_role_assignment.vault_access["operator"].role_definition_name == "Key Vault Secrets Officer" &&
      azurerm_role_assignment.vault_access["operator"].principal_id == var.operator_identity.principal_id &&
      azurerm_role_assignment.vault_access["operator"].principal_type == var.operator_identity.principal_type
    )
    error_message = "Only the operator identity may receive secret management access."
  }

  assert {
    condition = (
      azurerm_role_assignment.vault_access["runtime"].name == uuidv5("dns", "${var.name}/runtime") &&
      azurerm_role_assignment.vault_access["operator"].name == uuidv5("dns", "${var.name}/operator")
    )
    error_message = "Role assignment names must be stable per vault and purpose."
  }
}

run "rejects_shared_runtime_and_operator_principal" {
  command = plan

  variables {
    operator_identity = {
      principal_id   = "00000000-0000-4000-8000-000000000003"
      principal_type = "ServicePrincipal"
    }
  }

  expect_failures = [var.operator_identity]
}

run "rejects_invalid_operator_principal_type" {
  command = plan

  variables {
    operator_identity = {
      principal_id   = "00000000-0000-4000-8000-000000000004"
      principal_type = "Application"
    }
  }

  expect_failures = [var.operator_identity]
}

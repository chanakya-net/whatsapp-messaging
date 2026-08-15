mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-dev-cin-042"
      vault_uri = "https://kv-msgbr-dev-cin-042.vault.azure.net/"
    }
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  runtime_identity = {
    principal_id = "00000000-0000-4000-8000-000000000003"
    resource_id  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-worker-dev-cin-042"
  }
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
  tags = {
    owner       = "platform"
    project     = "caller-cannot-override"
    environment = "caller-cannot-override"
  }
}

run "wires_isolated_dev_vault_contract" {
  command = plan

  assert {
    condition     = local.resource_group_name == "rg-messagebridge-dev-centralindia-042" && output.vault_name == "kv-msgbr-dev-cin-042"
    error_message = "The dev vault must use deterministic environment-isolated names."
  }

  assert {
    condition     = local.mandatory_tags.project == "messagebridge" && local.mandatory_tags.environment == "dev" && local.mandatory_tags.owner == "platform"
    error_message = "Mandatory dev tags must override caller values while preserving safe tags."
  }

  assert {
    condition = alltrue([
      for name, reference in output.container_app_secret_references :
      reference.name == name &&
      reference.identity == var.runtime_identity.resource_id &&
      reference.key_vault_secret_id == "https://kv-msgbr-dev-cin-042.vault.azure.net/secrets/${name}"
    ])
    error_message = "Dev references must be versionless and use only the supplied runtime identity."
  }

  assert {
    condition     = output.alertable_resource_ids == toset([output.vault_id])
    error_message = "Only safe dev vault metadata may be re-exported."
  }
}

run "invalid_serial_stops" {
  command = plan

  variables {
    bootstrap_serial = "42"
  }

  expect_failures = [var.bootstrap_serial]
}

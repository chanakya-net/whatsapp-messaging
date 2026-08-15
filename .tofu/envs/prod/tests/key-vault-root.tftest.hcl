mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-prod-cin-042"
      vault_uri = "https://kv-msgbr-prod-cin-042.vault.azure.net/"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-prod-cin-042"
    }
  }
}

override_resource {
  target = azurerm_user_assigned_identity.runtime
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000006"
    client_id    = "00000000-0000-4000-8000-000000000016"
  }
}

override_resource {
  target = azurerm_user_assigned_identity.migrator
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-migrator-prod-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000007"
    client_id    = "00000000-0000-4000-8000-000000000017"
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  migration_image = {
    repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
    digest     = "4c1d7a1f0f1a4dbb9a1b3f6d5e2c8a7b6d4e3f2a1b0c9d8e7f6a5b4c3d2e1f00"
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

run "wires_isolated_prod_vault_contract" {
  command = plan

  assert {
    condition     = local.resource_group_name == "rg-messagebridge-prod-centralindia-042" && output.vault_name == "kv-msgbr-prod-cin-042"
    error_message = "The prod vault must use deterministic environment-isolated names."
  }

  assert {
    condition     = local.mandatory_tags.project == "messagebridge" && local.mandatory_tags.environment == "prod" && local.mandatory_tags.owner == "platform"
    error_message = "Mandatory prod tags must override caller values while preserving safe tags."
  }

  assert {
    condition = alltrue([
      for name, reference in output.container_app_secret_references :
      reference.name == name &&
      reference.identity == azurerm_user_assigned_identity.runtime.id &&
      reference.key_vault_secret_id == "https://kv-msgbr-prod-cin-042.vault.azure.net/secrets/${name}"
    ])
    error_message = "Prod references must be versionless and use only the supplied runtime identity."
  }

  assert {
    condition     = output.alertable_resource_ids == toset([output.vault_id])
    error_message = "Only safe prod vault metadata may be re-exported."
  }
}

run "invalid_serial_stops" {
  command = plan

  variables {
    bootstrap_serial = "0042"
  }

  expect_failures = [var.bootstrap_serial]
}

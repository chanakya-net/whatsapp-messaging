mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-dev-cin-042"
      vault_uri = "https://kv-msgbr-dev-cin-042.vault.azure.net/"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id                = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-dev-cin-042"
      default_domain    = "wittysky-1a2b3c4d.centralindia.azurecontainerapps.io"
      static_ip_address = "20.192.0.10"
    }
  }
}

# Per-resource overrides keep runtime and migrator computed values distinct so identity
# separation cannot be masked by a single type-wide mock.
override_resource {
  target = azurerm_user_assigned_identity.runtime
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-dev-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000003"
    client_id    = "00000000-0000-4000-8000-000000000013"
  }
}

override_resource {
  target = azurerm_user_assigned_identity.migrator
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-migrator-dev-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000005"
    client_id    = "00000000-0000-4000-8000-000000000015"
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
  tags = {
    owner = "platform"
  }
}

run "wires_isolated_dev_environment_and_identities" {
  command = plan

  assert {
    condition     = module.container_environment.environment_name == "cae-messagebridge-dev-cin-042"
    error_message = "The dev managed environment must use a deterministic environment-isolated name."
  }

  assert {
    condition     = azurerm_user_assigned_identity.runtime.name == "id-messagebridge-runtime-dev-cin-042" && azurerm_user_assigned_identity.migrator.name == "id-messagebridge-migrator-dev-cin-042"
    error_message = "Dev must create separately named runtime and migrator identities."
  }

  assert {
    condition = alltrue([
      for identity in [azurerm_user_assigned_identity.runtime, azurerm_user_assigned_identity.migrator] :
      identity.location == "centralindia" && identity.resource_group_name == "rg-messagebridge-dev-centralindia-042"
    ])
    error_message = "Dev identities must live in the dev Central India resource group."
  }

  assert {
    condition     = azurerm_user_assigned_identity.runtime.tags["purpose"] == "runtime" && azurerm_user_assigned_identity.migrator.tags["purpose"] == "migrator"
    error_message = "Dev identities must record their distinct purpose."
  }

  assert {
    condition = alltrue([
      for identity in [azurerm_user_assigned_identity.runtime, azurerm_user_assigned_identity.migrator] :
      identity.tags["project"] == "messagebridge" && identity.tags["environment"] == "dev" && identity.tags["owner"] == "platform"
    ])
    error_message = "Dev identities must carry mandatory tags alongside safe caller tags."
  }

  assert {
    condition     = output.runtime_identity.resource_id != output.migrator_identity.resource_id && output.runtime_identity.principal_id != output.migrator_identity.principal_id && output.runtime_identity.client_id != output.migrator_identity.client_id
    error_message = "The dev runtime and migrator identities must stay separate principals."
  }

  assert {
    condition     = output.runtime_identity.resource_id == azurerm_user_assigned_identity.runtime.id && output.migrator_identity.resource_id == azurerm_user_assigned_identity.migrator.id
    error_message = "Dev identity outputs must expose the root-owned identity contract."
  }

  assert {
    condition     = output.container_app_environment_id == module.container_environment.environment_id && output.container_app_environment_default_domain == "wittysky-1a2b3c4d.centralindia.azurecontainerapps.io"
    error_message = "Dev must export the managed environment identity and default domain."
  }

  assert {
    condition     = output.container_app_environment_static_ip_address == "20.192.0.10" && output.container_app_environment_outbound_ip_addresses == toset(["20.192.0.10"])
    error_message = "Dev must export the environment egress address contract."
  }

  assert {
    condition = alltrue([
      for name, reference in output.container_app_secret_references :
      reference.identity == azurerm_user_assigned_identity.runtime.id
    ])
    error_message = "Dev vault references must use the root-owned runtime identity and never the migrator."
  }
}

run "secret_bearing_tag_key_stops" {
  command = plan

  variables {
    tags = {
      owner                      = "platform"
      rabbitmq_connection_string = "not-a-tag"
    }
  }

  expect_failures = [var.tags]
}

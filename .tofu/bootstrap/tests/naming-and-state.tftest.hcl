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
  tenant_id         = "00000000-0000-4000-8000-000000000001"
  subscription_id   = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial  = "042"
  repository        = "chanakya-net/whatsapp-messaging"
  tags = {
    owner       = "platform"
    project     = "caller-cannot-override"
    environment = "caller-cannot-override"
  }
}

run "deterministic_names_and_mandatory_tags" {
  command = plan

  assert {
    condition     = azurerm_storage_account.state.name == "messagebridgetfstate042"
    error_message = "State account name must use the fixed project token and explicit serial."
  }

  assert {
    condition     = azurerm_resource_group.ownership["bootstrap"].name == "rg-messagebridge-bootstrap-centralindia-042"
    error_message = "Bootstrap resource group name is not deterministic."
  }

  assert {
    condition     = azurerm_resource_group.ownership["prod"].location == "centralindia"
    error_message = "All bootstrap resources must remain in centralindia."
  }

  assert {
    condition     = azurerm_resource_group.ownership["dev"].tags["project"] == "messagebridge" && azurerm_resource_group.ownership["dev"].tags["environment"] == "dev" && azurerm_resource_group.ownership["dev"].tags["owner"] == "platform"
    error_message = "Mandatory tags must override callers while preserving safe extra tags."
  }
}

run "state_is_hardened_and_separated" {
  command = plan

  assert {
    condition     = azurerm_storage_account.state.shared_access_key_enabled == false && azurerm_storage_account.state.allow_nested_items_to_be_public == false && azurerm_storage_account.state.min_tls_version == "TLS1_2"
    error_message = "State storage must require Azure AD, disable public blobs, and require TLS 1.2."
  }

  assert {
    condition     = azurerm_storage_account.state.blob_properties[0].versioning_enabled && azurerm_storage_account.state.blob_properties[0].delete_retention_policy[0].days >= 7
    error_message = "State blobs must have versioning and retention enabled."
  }

  assert {
    condition     = length(azurerm_storage_container.state) == 4 && toset(keys(azurerm_storage_container.state)) == toset(["bootstrap", "shared", "dev", "prod"])
    error_message = "Bootstrap, shared, dev, and prod require separate private containers."
  }

  assert {
    condition     = length(distinct([for backend in values(output.state_backends) : "${backend.container_name}/${backend.key}"])) == 4
    error_message = "Each root must have a distinct remote-state destination."
  }
}

run "invalid_serial_stops" {
  command = plan

  variables {
    bootstrap_serial = "42"
  }

  expect_failures = [var.bootstrap_serial]
}

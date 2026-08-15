mock_provider "azurerm" {
  mock_resource "azurerm_postgresql_flexible_server" {
    defaults = {
      id                  = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-shared-centralindia-042/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-messagebridge-shared-cin-042"
      fqdn                = "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
      private_dns_zone_id = null
    }
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  repository       = "chanakya-net/whatsapp-messaging"
  entra_administrator = {
    object_id      = "00000000-0000-4000-8000-000000000003"
    principal_name = "messagebridge-shared-operators"
    principal_type = "Group"
  }
  tags = {
    owner       = "platform"
    project     = "caller-cannot-override"
    environment = "caller-cannot-override"
  }
}

run "wires_shared_database_with_deterministic_metadata" {
  command = plan

  assert {
    condition     = output.server_name == "psql-messagebridge-shared-cin-042" && output.server_fqdn == "psql-messagebridge-shared-cin-042.postgres.database.azure.com"
    error_message = "The shared server name and endpoint must be deterministic."
  }

  assert {
    condition     = output.database_names == toset(["messagebridge_dev", "messagebridge_prod"])
    error_message = "The shared root must create isolated development and production databases."
  }

  assert {
    condition     = local.resource_group_name == "rg-messagebridge-shared-centralindia-042"
    error_message = "The shared root must target the bootstrap-owned shared resource group."
  }

  assert {
    condition     = local.mandatory_tags.project == "messagebridge" && local.mandatory_tags.environment == "shared" && local.mandatory_tags.owner == "platform"
    error_message = "Mandatory shared tags must override caller values while preserving safe tags."
  }
}

run "invalid_serial_stops" {
  command = plan

  variables {
    bootstrap_serial = "42"
  }

  expect_failures = [var.bootstrap_serial]
}

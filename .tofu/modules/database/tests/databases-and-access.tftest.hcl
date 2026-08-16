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
  name                = "psql-messagebridge-shared-cin-042"
  resource_group_name = "rg-messagebridge-shared-centralindia-042"
  location            = "centralindia"
  tenant_id           = "00000000-0000-4000-8000-000000000001"
  entra_administrator = {
    object_id      = "00000000-0000-4000-8000-000000000003"
    principal_name = "messagebridge-shared-operators"
    principal_type = "Group"
  }
  databases = {
    dev = {
      name      = "messagebridge_dev"
      charset   = "UTF8"
      collation = "en_US.utf8"
    }
    prod = {
      name      = "messagebridge_prod"
      charset   = "UTF8"
      collation = "en_US.utf8"
    }
  }
  reviewed_egress_ranges = {
    ip-203-0-113-10 = "203.0.113.10/32"
  }
}

run "creates_databases_and_entra_administrator" {
  command = plan

  assert {
    condition     = toset(keys(azurerm_postgresql_flexible_server_database.this)) == toset(["dev", "prod"])
    error_message = "Development and production databases must be distinct resources."
  }

  assert {
    condition     = alltrue([for database in values(azurerm_postgresql_flexible_server_database.this) : database.charset == "UTF8" && database.collation == "en_US.utf8"])
    error_message = "Application databases must use the approved UTF-8 locale."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server_active_directory_administrator.this.object_id == var.entra_administrator.object_id && azurerm_postgresql_flexible_server_active_directory_administrator.this.principal_name == var.entra_administrator.principal_name && azurerm_postgresql_flexible_server_active_directory_administrator.this.principal_type == var.entra_administrator.principal_type
    error_message = "The server must use the supplied non-secret Entra administrator metadata."
  }

  assert {
    condition     = output.postgres_firewall_ranges == var.reviewed_egress_ranges
    error_message = "Managed firewall outputs must reflect the required reviewed range map."
  }

  assert {
    condition     = output.server_name == var.name && output.server_fqdn == "psql-messagebridge-shared-cin-042.postgres.database.azure.com" && output.server_id == azurerm_postgresql_flexible_server.this.id
    error_message = "Outputs must expose only required server connection metadata."
  }

  assert {
    condition     = output.database_names == toset(["messagebridge_dev", "messagebridge_prod"]) && output.alertable_resource_ids == toset([azurerm_postgresql_flexible_server.this.id])
    error_message = "Outputs must expose database names and alertable resource IDs."
  }
}

run "creates_exact_reviewed_firewall_set" {
  command = plan

  variables {
    reviewed_egress_ranges = {
      ip-20-192-0-20 = "20.192.0.20/32"
      ip-20-192-0-30 = "20.192.0.30/32"
      ip-20-193-0-20 = "20.193.0.20/32"
    }
  }

  assert {
    condition = (
      toset(keys(azurerm_postgresql_flexible_server_firewall_rule.this)) == toset(keys(var.reviewed_egress_ranges)) &&
      alltrue([
        for key, rule in azurerm_postgresql_flexible_server_firewall_rule.this :
        rule.start_ip_address == trimsuffix(var.reviewed_egress_ranges[key], "/32") &&
        rule.end_ip_address == trimsuffix(var.reviewed_egress_ranges[key], "/32")
      ]) &&
      output.postgres_firewall_ranges == var.reviewed_egress_ranges
    )
    error_message = "Every reviewed /32 must create one independent exact-address firewall resource."
  }
}

run "rejects_broad_azure_services_firewall_rule" {
  command = plan

  variables {
    reviewed_egress_ranges = {
      ip-0-0-0-0 = "0.0.0.0/32"
    }
  }

  expect_failures = [var.reviewed_egress_ranges]
}

run "rejects_empty_reviewed_firewall_set" {
  command = plan

  variables {
    reviewed_egress_ranges = {}
  }

  expect_failures = [var.reviewed_egress_ranges]
}

run "rejects_invalid_or_non_32_ranges" {
  command = plan

  variables {
    reviewed_egress_ranges = {
      ip-203-0-113-999 = "203.0.113.999/32"
      ip-203-0-113-0   = "203.0.113.0/24"
      ip-v6            = "2001:db8::1/128"
    }
  }

  expect_failures = [var.reviewed_egress_ranges]
}

run "rejects_duplicate_ranges" {
  command = plan

  variables {
    reviewed_egress_ranges = {
      ip-203-0-113-10 = "203.0.113.10/32"
      duplicate       = "203.0.113.10/32"
    }
  }

  expect_failures = [var.reviewed_egress_ranges]
}

run "rejects_non_cidr_derived_keys" {
  command = plan

  variables {
    reviewed_egress_ranges = {
      arbitrary = "203.0.113.10/32"
    }
  }

  expect_failures = [var.reviewed_egress_ranges]
}

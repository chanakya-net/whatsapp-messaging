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
  reviewed_egress_ranges = {
    ip-203-0-113-10 = "203.0.113.10/32"
  }
}

run "server_uses_approved_low_cost_posture" {
  command = plan

  assert {
    condition     = azurerm_postgresql_flexible_server.this.version == "17" && azurerm_postgresql_flexible_server.this.sku_name == "B_Standard_B1ms"
    error_message = "The shared server must use PostgreSQL 17 on B1ms."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server.this.storage_mb == 32768 && azurerm_postgresql_flexible_server.this.auto_grow_enabled == false
    error_message = "The shared server must use fixed 32 GiB storage."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server.this.backup_retention_days == 14 && azurerm_postgresql_flexible_server.this.geo_redundant_backup_enabled == false
    error_message = "The shared server must retain local point-in-time backups for 14 days without geo backup."
  }

  assert {
    condition     = length(azurerm_postgresql_flexible_server.this.high_availability) == 0
    error_message = "High availability must remain disabled for the low-cost shared server."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server.this.public_network_access_enabled == true && azurerm_postgresql_flexible_server.this.delegated_subnet_id == null && azurerm_postgresql_flexible_server.this.private_dns_zone_id == null
    error_message = "The shared server must use public networking without VNet integration."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server.this.authentication[0].active_directory_auth_enabled == true && azurerm_postgresql_flexible_server.this.authentication[0].password_auth_enabled == false && azurerm_postgresql_flexible_server.this.authentication[0].tenant_id == var.tenant_id
    error_message = "The shared server must use Entra-only authentication."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server_configuration.require_secure_transport.value == "on" && azurerm_postgresql_flexible_server_configuration.ssl_min_protocol_version.value == "TLSv1.2"
    error_message = "The shared server must require TLS 1.2 transport."
  }
}

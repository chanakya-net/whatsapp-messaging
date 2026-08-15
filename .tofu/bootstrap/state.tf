resource "azurerm_resource_group" "ownership" {
  for_each = local.environments

  name     = local.resource_group_names[each.key]
  location = local.location
  tags = merge(var.tags, local.mandatory_tags, {
    environment = each.key
  })
}

resource "azurerm_storage_account" "state" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.ownership["bootstrap"].name
  location                 = azurerm_resource_group.ownership["bootstrap"].location
  account_tier             = "Standard"
  account_replication_type = "GRS"
  account_kind             = "StorageV2"

  allow_nested_items_to_be_public  = false
  cross_tenant_replication_enabled = false
  min_tls_version                  = "TLS1_2"
  shared_access_key_enabled        = false

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 30
    }

    container_delete_retention_policy {
      days = 30
    }
  }

  tags = merge(var.tags, local.mandatory_tags, {
    environment = "bootstrap"
    purpose     = "opentofu-state"
  })

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "state" {
  for_each = local.environments

  name                  = each.key
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
}

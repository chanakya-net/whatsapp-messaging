mock_provider "azurerm" {
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.KeyVault/vaults/kv-msgbr-prod-cin-042"
      vault_uri = "https://kv-msgbr-prod-cin-042.vault.azure.net/"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id                = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-prod-cin-042"
      default_domain    = "prod.internal.azurecontainerapps.io"
      static_ip_address = "20.193.0.10"
    }
  }

  mock_resource "azurerm_container_app" {
    defaults = {
      id                   = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/containerApps/ca-messagebridge-prod-cin-042"
      latest_revision_fqdn = "ca-messagebridge-prod-cin-042.internal.azurecontainerapps.io"
      latest_revision_name = "ca-messagebridge-prod-cin-042--revision"
      outbound_ip_addresses = [
        "20.193.0.20",
        "20.193.0.21",
      ]
    }
  }
}

override_resource {
  target = azurerm_user_assigned_identity.runtime
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-prod-cin-042"
    principal_id = "00000000-0000-4000-8000-000000000003"
    client_id    = "00000000-0000-4000-8000-000000000013"
  }
}

override_resource {
  target = azurerm_user_assigned_identity.migrator
  values = {
    id           = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-migrator-prod-cin-042"
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
  worker_allowed_tenant_ids = ["tenant-prod-b", "tenant-prod-a"]
  tags = {
    owner = "platform"
  }
}

run "wires_one_environment_isolated_prod_worker" {
  command = plan

  assert {
    condition = (
      local.worker_name == "ca-messagebridge-prod-cin-042" &&
      local.worker_database_host == "psql-messagebridge-shared-cin-042.postgres.database.azure.com" &&
      local.worker_database_name == "messagebridge_prod" &&
      !strcontains(local.worker_name, "dev") &&
      !strcontains(local.worker_database_name, "dev")
    )
    error_message = "Prod worker and shared database coordinates must be deterministic and contain no dev resource."
  }

  assert {
    condition = (
      local.worker_runtime_configuration.aspnetcore_environment == "Production" &&
      local.worker_runtime_configuration.topology_prefix == "prod" &&
      local.worker_runtime_configuration.retention_hours == 168 &&
      local.worker_runtime_configuration.allowed_tenant_ids == toset(var.worker_allowed_tenant_ids)
    )
    error_message = "Prod must supply its Production topology, 168-hour retention, and caller tenant set."
  }

  assert {
    condition = (
      var.worker_image.repository == "ghcr.io/tarampampam/error-pages" &&
      var.worker_image.digest == "f23f8042a2804669315fd232281d0ccecf1959332314a46e02ca2482064064a6"
    )
    error_message = "Prod must default to the verified digest-pinned public bootstrap image."
  }

  assert {
    condition = (
      toset(keys(local.worker_vault_references)) == toset(["rabbitmq", "new_relic"]) &&
      alltrue([
        for reference in values(local.worker_vault_references) :
        reference.identity == azurerm_user_assigned_identity.runtime.id &&
        !strcontains(reference.identity, "migrator") &&
        !strcontains(reference.identity, "dev") &&
        strcontains(reference.key_vault_secret_id, "kv-msgbr-prod-cin-042")
      ])
    )
    error_message = "Prod worker must select only prod versionless references using the prod runtime identity."
  }

  assert {
    condition = (
      module.worker.worker_name == "ca-messagebridge-prod-cin-042" &&
      output.worker_id == module.worker.worker_id &&
      output.worker_name == module.worker.worker_name &&
      output.worker_internal_fqdn == "ca-messagebridge-prod-cin-042.internal.azurecontainerapps.io" &&
      output.worker_latest_revision_name == "ca-messagebridge-prod-cin-042--revision" &&
      output.worker_outbound_ip_addresses == toset(["20.193.0.20", "20.193.0.21"]) &&
      output.worker_alertable_resource_ids == toset([module.worker.worker_id])
    )
    error_message = "Prod root must re-export only safe worker metadata for delivery, firewall, and alerts."
  }
}

run "invalid_worker_otlp_endpoint_stops" {
  command = plan

  variables {
    worker_otlp_endpoint = "http://insecure.example"
  }

  expect_failures = [var.worker_otlp_endpoint]
}

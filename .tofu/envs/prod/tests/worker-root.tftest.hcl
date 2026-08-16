mock_provider "azurerm" {
  mock_resource "azurerm_monitor_action_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.Insights/actionGroups/ag-messagebridge-prod-cin-042"
    }
  }

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

  mock_resource "azurerm_container_app_job" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/jobs/mig-messagebridge-prod-cin-042"
      outbound_ip_addresses = [
        "20.193.0.30",
        "20.193.0.31",
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

override_resource {
  target = module.worker.azurerm_container_app_job.migration
  values = {
    id                    = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/jobs/mig-messagebridge-prod-cin-042"
    outbound_ip_addresses = ["20.193.0.30", "20.193.0.31"]
  }
}

override_resource {
  target = module.worker.azurerm_container_app_job.smoke
  values = {
    id                    = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-prod-centralindia-042/providers/Microsoft.App/jobs/smoke-messagebridge-prod-cin-042"
    outbound_ip_addresses = ["20.193.0.40", "20.193.0.20"]
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
  alert_email      = "platform-alerts@example.com"
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
  worker_allowed_tenant_ids = ["tenant-prod-b", "tenant-prod-a"]
  migration_image = {
    repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
    digest     = "4c1d7a1f0f1a4dbb9a1b3f6d5e2c8a7b6d4e3f2a1b0c9d8e7f6a5b4c3d2e1f00"
  }
  tags = {
    owner = "platform"
  }
}

run "exports_complete_reviewed_postgres_egress" {
  command = plan

  assert {
    condition = output.reviewed_postgres_egress == {
      environment = "prod"
      sources = {
        container_environment = toset(["20.193.0.10/32"])
        worker                = toset(["20.193.0.20/32", "20.193.0.21/32"])
        migration             = toset(["20.193.0.30/32", "20.193.0.31/32"])
        smoke                 = toset(["20.193.0.20/32", "20.193.0.40/32"])
      }
      ranges = toset([
        "20.193.0.10/32",
        "20.193.0.20/32",
        "20.193.0.21/32",
        "20.193.0.30/32",
        "20.193.0.31/32",
        "20.193.0.40/32",
      ])
    }
    error_message = "Prod must publish its complete labelled, deduplicated PostgreSQL egress union."
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

run "wires_one_environment_isolated_prod_migration_job" {
  command = plan

  assert {
    condition = (
      local.migration_job_name == "mig-messagebridge-prod-cin-042" &&
      length(local.migration_job_name) < 32 &&
      local.migration_job_name != local.worker_name &&
      !strcontains(local.migration_job_name, "dev")
    )
    error_message = "Prod migration job must have a deterministic prod-only name within the Azure length limit."
  }

  assert {
    condition = (
      local.migration_database.host == local.worker_database_host &&
      local.migration_database.port == 5432 &&
      local.migration_database.name == "messagebridge_prod" &&
      local.migration_database.username == azurerm_user_assigned_identity.migrator.name &&
      local.migration_database.username != azurerm_user_assigned_identity.runtime.name &&
      !strcontains(local.migration_database.name, "dev") &&
      !strcontains(local.migration_database.username, "dev")
    )
    error_message = "Prod migration job must target the prod database as the prod migrator principal."
  }

  assert {
    condition = (
      local.migrator_identity.resource_id == azurerm_user_assigned_identity.migrator.id &&
      local.migrator_identity.principal_id == azurerm_user_assigned_identity.migrator.principal_id &&
      local.migrator_identity.client_id == azurerm_user_assigned_identity.migrator.client_id &&
      !strcontains(local.migrator_identity.resource_id, "runtime") &&
      !strcontains(local.migrator_identity.resource_id, "dev")
    )
    error_message = "Prod migration job must attach only the prod migrator identity."
  }

  assert {
    condition = (
      var.migration_image.repository == "ghcr.io/chanakya-net/whatsapp-messaging/migrate" &&
      can(regex("^[0-9a-f]{64}$", var.migration_image.digest)) &&
      var.migration_image.repository != var.worker_image.repository
    )
    error_message = "Prod must consume a caller-supplied immutable digest of the dedicated migration image."
  }

  assert {
    condition = (
      module.worker.migration_job_name == "mig-messagebridge-prod-cin-042" &&
      output.migration_job_id == module.worker.migration_job_id &&
      output.migration_job_name == module.worker.migration_job_name &&
      output.migration_job_outbound_ip_addresses == toset(["20.193.0.30", "20.193.0.31"]) &&
      !strcontains(output.migration_job_id, "dev")
    )
    error_message = "Prod root must re-export only safe prod migration job metadata."
  }

  assert {
    condition = (
      output.worker_latest_revision_name == "ca-messagebridge-prod-cin-042--revision" &&
      output.worker_outbound_ip_addresses == toset(["20.193.0.20", "20.193.0.21"]) &&
      output.worker_alertable_resource_ids == toset([module.worker.worker_id]) &&
      !contains(tolist(output.worker_alertable_resource_ids), module.worker.migration_job_id)
    )
    error_message = "Adding the migration job must not change the prod worker revision, egress, or alerting surface."
  }
}

run "mutable_prod_migration_image_stops" {
  command = plan

  variables {
    migration_image = {
      repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
      digest     = "latest"
    }
  }

  expect_failures = [var.migration_image]
}

run "foreign_prod_migration_repository_stops" {
  command = plan

  variables {
    migration_image = {
      repository = "ghcr.io/chanakya-net/whatsapp-messaging/worker"
      digest     = "4c1d7a1f0f1a4dbb9a1b3f6d5e2c8a7b6d4e3f2a1b0c9d8e7f6a5b4c3d2e1f00"
    }
  }

  expect_failures = [var.migration_image]
}

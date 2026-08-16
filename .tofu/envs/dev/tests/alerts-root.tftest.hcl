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
      default_domain    = "dev.internal.azurecontainerapps.io"
      static_ip_address = "20.192.0.10"
    }
  }

  mock_resource "azurerm_container_app" {
    defaults = {
      id                    = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/containerApps/ca-messagebridge-dev-cin-042"
      latest_revision_fqdn  = "ca-messagebridge-dev-cin-042.internal.azurecontainerapps.io"
      latest_revision_name  = "ca-messagebridge-dev-cin-042--revision"
      outbound_ip_addresses = ["20.192.0.20"]
    }
  }

  mock_resource "azurerm_container_app_job" {
    defaults = {
      id                    = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/jobs/job-messagebridge-dev-cin-042"
      outbound_ip_addresses = ["20.192.0.30"]
    }
  }
}

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
  alert_email      = "platform-alerts@example.com"
  operator_identity = {
    principal_id   = "00000000-0000-4000-8000-000000000004"
    principal_type = "Group"
  }
  migration_image = {
    repository = "ghcr.io/chanakya-net/whatsapp-messaging/migrate"
    digest     = "4c1d7a1f0f1a4dbb9a1b3f6d5e2c8a7b6d4e3f2a1b0c9d8e7f6a5b4c3d2e1f00"
  }
}

run "wires_one_email_action_group_and_environment_alerts" {
  command = plan

  assert {
    condition = (
      azurerm_monitor_action_group.alerts.name == "ag-messagebridge-dev-cin-042" &&
      azurerm_monitor_action_group.alerts.short_name == "msgbr-alerts" &&
      length(azurerm_monitor_action_group.alerts.email_receiver) == 1 &&
      azurerm_monitor_action_group.alerts.email_receiver[0].email_address == var.alert_email
    )
    error_message = "Dev must own exactly one email Action Group."
  }

  assert {
    condition = (
      length(module.metric_alerts.alert_definitions) == 5 &&
      length([for alert in values(module.metric_alerts.alert_definitions) : alert if alert.metric_namespace == "Microsoft.App/containerapps"]) == 3 &&
      length([for alert in values(module.metric_alerts.alert_definitions) : alert if alert.metric_namespace == "Microsoft.App/jobs"]) == 2
    )
    error_message = "Dev must wire three worker alerts and one failure alert per migration/smoke job."
  }

  assert {
    condition = (
      module.metric_alerts.alert_definitions["job.migration.Executions"].scope_id == module.worker.migration_job_id &&
      module.metric_alerts.alert_definitions["job.migration.Executions"].severity == 1 &&
      module.metric_alerts.alert_definitions["job.smoke.Executions"].scope_id == module.worker.smoke_job_id &&
      module.metric_alerts.alert_definitions["job.smoke.Executions"].severity == 2
    )
    error_message = "Dev job alerts must retain job ownership and reviewed severities."
  }
}

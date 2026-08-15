mock_provider "azurerm" {
  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-bootstrap-centralindia-042/providers/Microsoft.Storage/storageAccounts/messagebridgetfstate042"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-bootstrap-centralindia-042/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock"
    }
  }
}

variables {
  tenant_id        = "00000000-0000-4000-8000-000000000001"
  subscription_id  = "00000000-0000-4000-8000-000000000002"
  bootstrap_serial = "042"
}

run "creates_four_workflow_identities" {
  command = plan

  assert {
    condition     = toset(keys(azurerm_user_assigned_identity.workflow)) == toset(["plan", "shared", "dev", "prod"])
    error_message = "Plan, shared, dev, and prod workflows require separate identities."
  }

  assert {
    condition     = azurerm_user_assigned_identity.workflow["plan"].name == "id-messagebridge-plan-cin-042" && azurerm_user_assigned_identity.workflow["prod"].name == "id-messagebridge-prod-cin-042"
    error_message = "Identity names must be deterministic and environment-specific."
  }
}

run "federation_is_exactly_scoped" {
  command = plan

  assert {
    condition     = alltrue([for credential in values(azurerm_federated_identity_credential.workflow) : credential.issuer == "https://token.actions.githubusercontent.com" && toset(credential.audience) == toset(["api://AzureADTokenExchange"])])
    error_message = "Every identity must trust only GitHub's issuer and Azure's token-exchange audience."
  }

  assert {
    condition     = azurerm_federated_identity_credential.workflow["plan"].subject == "repo:chanakya-net/whatsapp-messaging:pull_request"
    error_message = "The read-only plan identity must trust pull requests only."
  }

  assert {
    condition = alltrue([
      for environment in ["shared", "dev", "prod"] :
      azurerm_federated_identity_credential.workflow[environment].subject == "repo:chanakya-net/whatsapp-messaging:environment:${environment}"
    ])
    error_message = "Mutating identities must trust only their matching protected GitHub environment."
  }

  assert {
    condition     = length([for credential in values(azurerm_federated_identity_credential.workflow) : credential.subject if strcontains(credential.subject, "ref:") || strcontains(credential.subject, "*")]) == 0
    error_message = "Branch and wildcard federated subjects are forbidden."
  }
}

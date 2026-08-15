output "location" {
  description = "Azure region used by every bootstrap-owned resource."
  value       = local.location
}

output "resource_groups" {
  description = "Environment ownership boundaries."
  value = {
    for environment, group in azurerm_resource_group.ownership : environment => {
      id   = group.id
      name = group.name
    }
  }
}

output "state_backends" {
  description = "Non-secret remote-state coordinates for each OpenTofu root."
  value       = local.state_backends
}

output "storage_account_id" {
  description = "Resource ID of the protected state account."
  value       = azurerm_storage_account.state.id
}

output "workflow_identities" {
  description = "Non-secret identifiers for GitHub OIDC workflow identities."
  value = {
    for purpose, identity in azurerm_user_assigned_identity.workflow : purpose => {
      client_id    = identity.client_id
      principal_id = identity.principal_id
      resource_id  = identity.id
    }
  }
}

output "custom_roles" {
  description = "Custom role definition IDs and their subscription-level assignable scope."
  value = {
    for purpose, role in azurerm_role_definition.operations : purpose => {
      id                = role.id
      assignable_scopes = role.assignable_scopes
    }
  }
}

output "workflow_role_assignments" {
  description = "Auditable non-secret workflow principal, role, and scope matrix."
  value = {
    for name, assignment in local.role_assignments : name => {
      principal          = assignment.principal_key
      role_definition_id = assignment.role_definition_id
      scope              = assignment.scope
    }
  }
}

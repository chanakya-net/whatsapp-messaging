variable "name" {
  description = "Globally unique name for the environment Key Vault."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,22}[a-z0-9]$", var.name)) && !strcontains(var.name, "--")
    error_message = "name must be a 3-24 character lowercase Azure Key Vault name."
  }
}

variable "resource_group_name" {
  description = "Existing environment resource group that owns the vault."
  type        = string
}

variable "location" {
  description = "Azure region for the environment vault."
  type        = string
}

variable "tenant_id" {
  description = "Azure tenant containing the environment identities."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a UUID."
  }
}

variable "runtime_identity" {
  description = "Non-secret metadata for the environment runtime managed identity."
  type = object({
    principal_id = string
    resource_id  = string
  })

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.runtime_identity.principal_id))
    error_message = "runtime_identity.principal_id must be a UUID."
  }

  validation {
    condition     = can(regex("(?i)^/subscriptions/[0-9a-f-]+/resourcegroups/[^/]+/providers/microsoft\\.managedidentity/userassignedidentities/[^/]+$", var.runtime_identity.resource_id))
    error_message = "runtime_identity.resource_id must identify an Azure user-assigned managed identity."
  }
}

variable "operator_identity" {
  description = "Non-secret metadata for the human-operated secret management principal."
  type = object({
    principal_id   = string
    principal_type = string
  })

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.operator_identity.principal_id))
    error_message = "operator_identity.principal_id must be a UUID."
  }

  validation {
    condition     = contains(["Group", "ServicePrincipal", "User"], var.operator_identity.principal_type)
    error_message = "operator_identity.principal_type must be Group, ServicePrincipal, or User."
  }

  validation {
    condition     = var.operator_identity.principal_id != var.runtime_identity.principal_id
    error_message = "operator_identity and runtime_identity must use different principals."
  }
}

variable "tags" {
  description = "Non-sensitive resource tags."
  type        = map(string)
  default     = {}
}

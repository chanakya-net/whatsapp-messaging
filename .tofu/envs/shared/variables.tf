variable "tenant_id" {
  description = "Azure tenant containing the shared resources."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a UUID."
  }
}

variable "subscription_id" {
  description = "Azure subscription containing the shared resources."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.subscription_id))
    error_message = "subscription_id must be a UUID."
  }
}

variable "bootstrap_serial" {
  description = "Three-digit suffix matching the bootstrap-owned resource group."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{3}$", var.bootstrap_serial))
    error_message = "bootstrap_serial must contain exactly three digits."
  }
}

variable "repository" {
  description = "Repository responsible for the shared resources."
  type        = string
  default     = "chanakya-net/whatsapp-messaging"

  validation {
    condition     = var.repository == "chanakya-net/whatsapp-messaging"
    error_message = "Only chanakya-net/whatsapp-messaging may manage these resources."
  }
}

variable "tags" {
  description = "Additional non-sensitive resource tags. Mandatory tags take precedence."
  type        = map(string)
  default     = {}

  validation {
    condition = alltrue([
      for key in keys(var.tags) : length(regexall("(?i)(secret|token|password|credential|connection)", key)) == 0
    ])
    error_message = "Tag keys must not describe secret-bearing values."
  }
}

variable "entra_administrator" {
  description = "Non-secret metadata for the PostgreSQL Entra administrator."
  type = object({
    object_id      = string
    principal_name = string
    principal_type = string
  })

  validation {
    condition     = contains(["Group", "ServicePrincipal", "User"], var.entra_administrator.principal_type)
    error_message = "entra_administrator.principal_type must be Group, ServicePrincipal, or User."
  }
}

variable "firewall_rules" {
  description = "Explicit public-network endpoints permitted to reach PostgreSQL."
  type = map(object({
    start_ip_address = string
    end_ip_address   = string
  }))
  default = {}
}

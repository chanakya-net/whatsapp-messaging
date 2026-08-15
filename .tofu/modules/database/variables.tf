variable "name" {
  description = "Globally unique PostgreSQL Flexible Server name."
  type        = string
}

variable "resource_group_name" {
  description = "Existing resource group that owns the server."
  type        = string
}

variable "location" {
  description = "Azure region for the shared server."
  type        = string
}

variable "tenant_id" {
  description = "Azure tenant used for Entra-only database authentication."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a UUID."
  }
}

variable "entra_administrator" {
  description = "Non-secret metadata for the server Entra administrator."
  type = object({
    object_id      = string
    principal_name = string
    principal_type = string
  })
}

variable "tags" {
  description = "Non-sensitive resource tags."
  type        = map(string)
  default     = {}
}

variable "databases" {
  description = "Application databases to create on the shared server."
  type = map(object({
    name      = string
    charset   = string
    collation = string
  }))
  default = {}
}

variable "firewall_rules" {
  description = "Explicit public-network CIDR endpoints permitted to reach the server."
  type = map(object({
    start_ip_address = string
    end_ip_address   = string
  }))
  default = {}

  validation {
    condition = alltrue([
      for rule in values(var.firewall_rules) :
      !(rule.start_ip_address == "0.0.0.0" && rule.end_ip_address == "0.0.0.0")
    ])
    error_message = "firewall_rules must not contain Azure's broad 0.0.0.0 access rule."
  }
}

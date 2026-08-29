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

variable "reviewed_egress_ranges" {
  description = "Complete reviewed PostgreSQL egress set keyed by canonical IPv4 /32 range."
  type        = map(string)

  validation {
    condition     = length(var.reviewed_egress_ranges) > 0
    error_message = "reviewed_egress_ranges must contain the complete non-empty reviewed set."
  }

  validation {
    condition     = length(values(var.reviewed_egress_ranges)) == length(distinct(values(var.reviewed_egress_ranges)))
    error_message = "reviewed_egress_ranges must not contain duplicate ranges."
  }

  validation {
    condition = alltrue([
      for range in values(var.reviewed_egress_ranges) :
      can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+/32$", range)) &&
      try(cidrhost(range, 0), "") == trimsuffix(range, "/32")
    ])
    error_message = "reviewed_egress_ranges must contain canonical IPv4 /32 CIDRs only."
  }

  validation {
    condition     = !contains(values(var.reviewed_egress_ranges), "0.0.0.0/32")
    error_message = "reviewed_egress_ranges must not contain Azure's broad 0.0.0.0 access rule."
  }

  validation {
    condition = alltrue([
      for key, range in var.reviewed_egress_ranges :
      key == "ip-${replace(trimsuffix(range, "/32"), ".", "-")}"
    ])
    error_message = "reviewed_egress_ranges keys must use the stable ip-A-B-C-D form derived from each CIDR."
  }
}

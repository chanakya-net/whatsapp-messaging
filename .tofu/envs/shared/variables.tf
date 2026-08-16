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

variable "reviewed_egress_ranges" {
  description = "Complete delivery-reviewed PostgreSQL egress set keyed by canonical IPv4 /32 range."
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

variable "tenant_id" {
  description = "Azure tenant containing the bootstrap resources."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a UUID."
  }
}
variable "subscription_id" {
  description = "Azure subscription containing the bootstrap resources."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", var.subscription_id))
    error_message = "subscription_id must be a UUID."
  }
}

variable "bootstrap_serial" {
  description = "Operator-selected three-digit suffix for globally unique names. Change explicitly after a collision."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{3}$", var.bootstrap_serial))
    error_message = "bootstrap_serial must contain exactly three digits; automatic suffix selection is forbidden."
  }
}

variable "repository" {
  description = "GitHub repository trusted by the federated credentials."
  type        = string
  default     = "chanakya-net/whatsapp-messaging"

  validation {
    condition     = var.repository == "chanakya-net/whatsapp-messaging"
    error_message = "Only chanakya-net/whatsapp-messaging may use these identities."
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

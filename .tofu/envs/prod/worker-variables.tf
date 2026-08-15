variable "worker_image" {
  description = "Immutable worker image; defaults to the verified public bootstrap HTTP server."
  type = object({
    repository = string
    digest     = string
  })
  default = {
    repository = "ghcr.io/tarampampam/error-pages"
    digest     = "f23f8042a2804669315fd232281d0ccecf1959332314a46e02ca2482064064a6"
  }

  validation {
    condition = (
      can(regex("^[a-z0-9.-]+(?::[0-9]+)?(?:/[a-z0-9._-]+)+$", var.worker_image.repository)) &&
      !strcontains(var.worker_image.repository, "@") &&
      can(regex("^[0-9a-f]{64}$", var.worker_image.digest))
    )
    error_message = "worker_image must contain an untagged repository and lowercase 64-character sha256 digest."
  }
}

variable "migration_image" {
  description = "Immutable dedicated migration image; supplied per deployment and never defaulted to a placeholder."
  type = object({
    repository = string
    digest     = string
  })

  validation {
    condition = (
      var.migration_image.repository == "ghcr.io/chanakya-net/whatsapp-messaging/migrate" &&
      can(regex("^[0-9a-f]{64}$", var.migration_image.digest))
    )
    error_message = "migration_image must be the dedicated untagged migration repository with a lowercase 64-character sha256 digest."
  }
}

variable "worker_otlp_endpoint" {
  description = "Non-secret OTLP/HTTP endpoint used by the prod worker."
  type        = string
  default     = "https://otlp.nr-data.net:4318"

  validation {
    condition     = can(regex("^https://[^[:space:]]+$", var.worker_otlp_endpoint))
    error_message = "worker_otlp_endpoint must be an absolute HTTPS URL."
  }
}

variable "worker_allowed_tenant_ids" {
  description = "Tenant IDs accepted by the prod worker; empty fails tenant traffic closed."
  type        = set(string)
  default     = []

  validation {
    condition = alltrue([
      for tenant_id in var.worker_allowed_tenant_ids :
      trimspace(tenant_id) != "" && !strcontains(tenant_id, ",")
    ])
    error_message = "worker_allowed_tenant_ids cannot contain blank or comma-delimited values."
  }
}

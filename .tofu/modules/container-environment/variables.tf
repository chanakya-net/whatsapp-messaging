variable "name" {
  description = "Name of the environment-scoped Container Apps managed environment."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{0,30}[a-z0-9]$", var.name)) && !strcontains(var.name, "--")
    error_message = "name must be a 2-32 character lowercase Azure Container Apps environment name."
  }
}

variable "resource_group_name" {
  description = "Existing environment resource group that owns the managed environment."
  type        = string
}

variable "location" {
  description = "Azure region for the managed environment."
  type        = string

  validation {
    condition     = var.location == "centralindia"
    error_message = "location must be centralindia; workloads are single-region by design."
  }
}

variable "tags" {
  description = "Mandatory non-sensitive resource tags supplied by the environment root."
  type        = map(string)

  validation {
    condition = alltrue([
      for key in ["project", "environment", "location", "repository", "managed_by"] :
      try(trimspace(var.tags[key]), "") != ""
    ])
    error_message = "tags must include non-empty project, environment, location, repository, and managed_by values."
  }

  validation {
    condition = alltrue([
      for key in keys(var.tags) : length(regexall("(?i)(secret|token|password|credential|connection)", key)) == 0
    ])
    error_message = "Tag keys must not describe secret-bearing values."
  }
}

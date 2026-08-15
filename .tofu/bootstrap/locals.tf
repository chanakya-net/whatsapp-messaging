locals {
  project      = "messagebridge"
  location     = "centralindia"
  region_token = "cin"
  environments = toset(["bootstrap", "shared", "dev", "prod"])

  storage_account_name = "${local.project}tfstate${var.bootstrap_serial}"
  resource_group_names = {
    for environment in local.environments :
    environment => "rg-${local.project}-${environment}-${local.location}-${var.bootstrap_serial}"
  }

  mandatory_tags = {
    project    = local.project
    location   = local.location
    repository = var.repository
    managed_by = "opentofu-bootstrap"
  }

  workflow_identities = {
    plan = {
      subject     = "repo:${var.repository}:pull_request"
      environment = "shared"
    }
    shared = {
      subject     = "repo:${var.repository}:environment:shared"
      environment = "shared"
    }
    dev = {
      subject     = "repo:${var.repository}:environment:dev"
      environment = "dev"
    }
    prod = {
      subject     = "repo:${var.repository}:environment:prod"
      environment = "prod"
    }
  }

  subscription_scope = "/subscriptions/${var.subscription_id}"
  custom_role_definition_ids = {
    plan        = "ed94ef20-f4a4-4a9f-9b8c-301b2fb9d560"
    shared      = "760e27aa-7413-4f13-8e34-44059cf44b8c"
    environment = "5fa366da-fb95-4f6e-b57c-0cbb6a9d13ef"
  }
  custom_role_ids = {
    for purpose, role_id in local.custom_role_definition_ids :
    purpose => "${local.subscription_scope}/providers/Microsoft.Authorization/roleDefinitions/${role_id}"
  }
  builtin_role_ids = {
    blob_reader      = "${local.subscription_scope}/providers/Microsoft.Authorization/roleDefinitions/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"
    blob_contributor = "${local.subscription_scope}/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"
  }
  control_scopes = {
    for environment in ["shared", "dev", "prod"] :
    environment => "${local.subscription_scope}/resourceGroups/${local.resource_group_names[environment]}"
  }
  state_scopes = {
    for environment in ["shared", "dev", "prod"] :
    environment => "${azurerm_storage_account.state.id}/blobServices/default/containers/${environment}"
  }
  plan_control_assignments = {
    for environment in ["shared", "dev", "prod"] : "plan-${environment}-control" => {
      principal_key      = "plan"
      scope              = local.control_scopes[environment]
      role_definition_id = local.custom_role_ids.plan
    }
  }
  plan_state_assignments = {
    for environment in ["shared", "dev", "prod"] : "plan-${environment}-state" => {
      principal_key      = "plan"
      scope              = local.state_scopes[environment]
      role_definition_id = local.builtin_role_ids.blob_reader
    }
  }
  apply_control_assignments = {
    for environment in ["shared", "dev", "prod"] : "${environment}-control" => {
      principal_key      = environment
      scope              = local.control_scopes[environment]
      role_definition_id = environment == "shared" ? local.custom_role_ids.shared : local.custom_role_ids.environment
    }
  }
  apply_state_assignments = {
    for environment in ["shared", "dev", "prod"] : "${environment}-state" => {
      principal_key      = environment
      scope              = local.state_scopes[environment]
      role_definition_id = local.builtin_role_ids.blob_contributor
    }
  }
  role_assignments = merge(
    local.plan_control_assignments,
    local.plan_state_assignments,
    local.apply_control_assignments,
    local.apply_state_assignments
  )

  state_backends = {
    for environment in local.environments : environment => {
      resource_group_name  = local.resource_group_names.bootstrap
      storage_account_name = local.storage_account_name
      container_name       = environment
      key                  = "${local.project}/${environment}.tfstate"
      use_azuread_auth     = true
    }
  }
}

locals {
  project      = "messagebridge"
  environment  = "dev"
  location     = "centralindia"
  region_token = "cin"

  resource_group_name = "rg-${local.project}-${local.environment}-${local.location}-${var.bootstrap_serial}"

  # Azure Key Vault names allow at most 24 characters, so msgbr is the stable project token.
  vault_name = "kv-msgbr-${local.environment}-${local.region_token}-${var.bootstrap_serial}"

  container_app_environment_name = "cae-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  runtime_identity_name          = "id-${local.project}-runtime-${local.environment}-${local.region_token}-${var.bootstrap_serial}"
  migrator_identity_name         = "id-${local.project}-migrator-${local.environment}-${local.region_token}-${var.bootstrap_serial}"

  mandatory_tags = merge(var.tags, {
    project     = local.project
    environment = local.environment
    location    = local.location
    repository  = var.repository
    managed_by  = "opentofu"
  })
}

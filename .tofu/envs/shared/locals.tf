locals {
  project      = "messagebridge"
  environment  = "shared"
  location     = "centralindia"
  region_token = "cin"

  resource_group_name = "rg-${local.project}-${local.environment}-${local.location}-${var.bootstrap_serial}"
  server_name         = "psql-${local.project}-${local.environment}-${local.region_token}-${var.bootstrap_serial}"

  mandatory_tags = merge(var.tags, {
    project     = local.project
    environment = local.environment
    location    = local.location
    repository  = var.repository
    managed_by  = "opentofu"
  })

  databases = {
    dev = {
      name      = "messagebridge_dev"
      charset   = "UTF8"
      collation = "en_US.utf8"
    }
    prod = {
      name      = "messagebridge_prod"
      charset   = "UTF8"
      collation = "en_US.utf8"
    }
  }
}

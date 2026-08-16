module "database" {
  source = "../../modules/database"

  name                   = local.server_name
  resource_group_name    = local.resource_group_name
  location               = local.location
  tenant_id              = var.tenant_id
  entra_administrator    = var.entra_administrator
  databases              = local.databases
  reviewed_egress_ranges = var.reviewed_egress_ranges
  tags                   = local.mandatory_tags
}

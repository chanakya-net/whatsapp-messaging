module "database" {
  source = "../../modules/database"

  name                = local.server_name
  resource_group_name = local.resource_group_name
  location            = local.location
  tenant_id           = var.tenant_id
  entra_administrator = var.entra_administrator
  databases           = local.databases
  firewall_rules      = var.firewall_rules
  tags                = local.mandatory_tags
}

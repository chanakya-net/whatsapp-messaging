module "container_environment" {
  source = "../../modules/container-environment"

  name                = local.container_app_environment_name
  resource_group_name = local.resource_group_name
  location            = local.location
  tags                = local.mandatory_tags
}

# Consumption-only managed environment. Log Analytics, Application Insights, virtual network
# integration, internal load balancing, and zone redundancy are deliberately omitted so the
# environment stays at the serverless floor cost with no shared observability plane.
resource "azurerm_container_app_environment" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  tags = var.tags
}

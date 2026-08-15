mock_provider "azurerm" {
  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id                = "/subscriptions/00000000-0000-4000-8000-000000000002/resourceGroups/rg-messagebridge-dev-centralindia-042/providers/Microsoft.App/managedEnvironments/cae-messagebridge-dev-cin-042"
      default_domain    = "wittysky-1a2b3c4d.centralindia.azurecontainerapps.io"
      static_ip_address = "20.192.0.10"
    }
  }
}

variables {
  name                = "cae-messagebridge-dev-cin-042"
  resource_group_name = "rg-messagebridge-dev-centralindia-042"
  location            = "centralindia"
  tags = {
    project     = "messagebridge"
    environment = "dev"
    location    = "centralindia"
    repository  = "chanakya-net/whatsapp-messaging"
    managed_by  = "opentofu"
  }
}

run "creates_consumption_environment_and_typed_outputs" {
  command = plan

  assert {
    condition     = azurerm_container_app_environment.this.name == var.name && azurerm_container_app_environment.this.resource_group_name == var.resource_group_name
    error_message = "The managed environment must use the caller-supplied name inside the existing environment resource group."
  }

  assert {
    condition     = azurerm_container_app_environment.this.location == "centralindia"
    error_message = "The managed environment must stay in Central India."
  }

  assert {
    condition     = length(azurerm_container_app_environment.this.workload_profile) == 1
    error_message = "The managed environment must declare exactly one workload profile."
  }

  assert {
    condition     = one(azurerm_container_app_environment.this.workload_profile).name == "Consumption" && one(azurerm_container_app_environment.this.workload_profile).workload_profile_type == "Consumption"
    error_message = "The only workload profile must be serverless Consumption."
  }

  assert {
    condition     = one(azurerm_container_app_environment.this.workload_profile).minimum_count == null && one(azurerm_container_app_environment.this.workload_profile).maximum_count == null
    error_message = "Consumption must not reserve dedicated instance counts."
  }

  assert {
    # logs_destination is provider-computed when omitted, so only a configured value can be
    # asserted here; the static policy scanner rejects any assignment of it in module sources.
    condition     = azurerm_container_app_environment.this.log_analytics_workspace_id == null && azurerm_container_app_environment.this.logs_destination != "log-analytics"
    error_message = "The managed environment must not ship logs to a Log Analytics workspace."
  }

  assert {
    condition     = azurerm_container_app_environment.this.dapr_application_insights_connection_string == null
    error_message = "The managed environment must not attach Application Insights telemetry."
  }

  assert {
    condition     = azurerm_container_app_environment.this.infrastructure_subnet_id == null && azurerm_container_app_environment.this.infrastructure_resource_group_name == null
    error_message = "The managed environment must not integrate a virtual network."
  }

  assert {
    condition     = azurerm_container_app_environment.this.internal_load_balancer_enabled != true && azurerm_container_app_environment.this.zone_redundancy_enabled != true && azurerm_container_app_environment.this.mutual_tls_enabled != true
    error_message = "Optional load balancer, zone redundancy, and mTLS add-ons must stay disabled."
  }

  assert {
    condition     = length(azurerm_container_app_environment.this.identity) == 0
    error_message = "The managed environment must not carry its own identity assignment."
  }

  assert {
    condition = alltrue([
      for key in ["project", "environment", "location", "repository", "managed_by"] :
      azurerm_container_app_environment.this.tags[key] == var.tags[key]
    ])
    error_message = "The managed environment must carry every mandatory tag."
  }

  assert {
    condition     = output.environment_id == azurerm_container_app_environment.this.id && output.environment_name == var.name
    error_message = "Environment outputs must expose the created resource identity."
  }

  assert {
    condition     = output.default_domain == "wittysky-1a2b3c4d.centralindia.azurecontainerapps.io" && output.static_ip_address == "20.192.0.10"
    error_message = "Environment outputs must expose the provider-computed domain and static address."
  }

  assert {
    condition     = output.outbound_ip_addresses == toset(["20.192.0.10"])
    error_message = "The outbound address contract must be a set of environment egress addresses."
  }

  assert {
    condition     = output.alertable_resource_ids == toset([output.environment_id])
    error_message = "Only the managed environment may be re-exported for later alerting."
  }
}

run "invalid_name_stops" {
  command = plan

  variables {
    name = "CAE_MessageBridge_Dev"
  }

  expect_failures = [var.name]
}

run "non_central_india_location_stops" {
  command = plan

  variables {
    location = "eastus"
  }

  expect_failures = [var.location]
}

run "missing_mandatory_tag_stops" {
  command = plan

  variables {
    tags = {
      project     = "messagebridge"
      environment = "dev"
      location    = "centralindia"
      managed_by  = "opentofu"
    }
  }

  expect_failures = [var.tags]
}

run "secret_bearing_tag_key_stops" {
  command = plan

  variables {
    tags = {
      project           = "messagebridge"
      environment       = "dev"
      location          = "centralindia"
      repository        = "chanakya-net/whatsapp-messaging"
      managed_by        = "opentofu"
      connection_string = "not-a-tag"
    }
  }

  expect_failures = [var.tags]
}

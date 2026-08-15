resource "azurerm_role_assignment" "workflow" {
  for_each = local.role_assignments

  name                             = uuidv5("dns", "${var.repository}/${var.bootstrap_serial}/${each.key}")
  scope                            = each.value.scope
  role_definition_id               = each.value.role_definition_id
  principal_id                     = azurerm_user_assigned_identity.workflow[each.value.principal_key].principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true

  depends_on = [
    azurerm_role_definition.operations,
    azurerm_storage_container.state,
  ]
}

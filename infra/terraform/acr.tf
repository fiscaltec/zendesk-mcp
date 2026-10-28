resource "azurerm_container_registry" "acr" {
  name                          = var.acr_name
  resource_group_name           = azurerm_resource_group.rg.name
  location                      = var.location
  sku                           = "Premium" # private endpoints and CMK both need Premium
  admin_enabled                 = false
  public_network_access_enabled = false
  # Trusted Azure services through with public access off, as in the Vitally estate (it keeps ACR
  # Tasks usable). Images arrive by `az acr import` — deploy.yml, and the first image in the apply
  # procedure — which runs server-side and needs no data-plane path from the caller.
  network_rule_bypass_option = "AzureServices"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  # Customer-managed key encryption (versionless key id = auto-rotation).
  encryption {
    key_vault_key_id   = azurerm_key_vault_key.acr_cmk.versionless_id
    identity_client_id = azurerm_user_assigned_identity.app.client_id
  }

  depends_on = [azurerm_role_assignment.mi_cmk_crypto]
}

resource "azurerm_private_endpoint" "acr" {
  name                = "${var.name_prefix}-pe-acr-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  subnet_id           = azurerm_subnet.pe.id

  private_service_connection {
    name                           = "acr-conn"
    private_connection_resource_id = azurerm_container_registry.acr.id
    subresource_names              = ["registry"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "acr-zg"
    private_dns_zone_ids = [azurerm_private_dns_zone.acr.id]
  }
}

resource "azurerm_monitor_diagnostic_setting" "acr" {
  name                       = "to-law"
  target_resource_id         = azurerm_container_registry.acr.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  enabled_log { category = "ContainerRegistryRepositoryEvents" }
  enabled_log { category = "ContainerRegistryLoginEvents" }
  enabled_metric { category = "AllMetrics" }
}

# The registry is the Vitally MCP estate's, `vitallyproducruksouth` in `vitally-prod-rg-uksouth`,
# SHARED rather than duplicated (decided 2026-10-28). This configuration does not manage it: it adds
# a private endpoint into this VNet, and grants this estate's identity the roles it needs on it.
# Images live in their own repository, `zendesk-mcp`, beside Vitally's `vitally-mcp`.
#
# ⚠️ THE COST OF SHARING, accepted with the decision: `az acr import` needs Contributor on the
# REGISTRY (AcrPush lacks importImage, and ACR cannot scope that action to one repository), so this
# estate's identity — the apps' runtime identity as well as the deploy identity — can overwrite
# Vitally's tags. A Container App re-resolves its tag when a replica starts, and Vitally's staging
# scales to zero, so an overwritten `vitally-mcp` tag would be served at its next cold start. A
# compromise of this estate's identity therefore reaches the Vitally MCP server's code. The
# alternative is a Premium registry of this estate's own (about £40 a month); acr.tf in git history
# before this change built one, with CMK, and could be restored as it was.
#
# Vitally's Terraform owns the registry itself, its CMK, its diagnostics and its own private
# endpoint. None of what is added here is an attribute of the registry resource, so neither
# configuration plans changes to the other's objects.
data "azurerm_container_registry" "acr" {
  name                = var.acr_name
  resource_group_name = var.acr_resource_group_name
}

# A second private endpoint onto the same registry, from this VNet. The Vitally endpoint registers
# in the Vitally estate's privatelink.azurecr.io zone, linked only to its VNet; this one registers in
# this estate's zone (network.tf), linked only to this VNet, so each estate resolves the registry to
# its own endpoint. `is_manual_connection = false` approves the connection on creation, which needs
# the applying principal to hold rights on the registry (README.md, Prerequisites).
resource "azurerm_private_endpoint" "acr" {
  name                = "${var.name_prefix}-pe-acr-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  subnet_id           = azurerm_subnet.pe.id

  private_service_connection {
    name                           = "acr-conn"
    private_connection_resource_id = data.azurerm_container_registry.acr.id
    subresource_names              = ["registry"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "acr-zg"
    private_dns_zone_ids = [azurerm_private_dns_zone.acr.id]
  }
}

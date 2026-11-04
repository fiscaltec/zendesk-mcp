# The resource group is created and owned by this configuration: the Zendesk MCP estate shares the
# subscription and tenant with the Vitally MCP estate but nothing inside a resource group, so
# destroying or recreating one can never touch the other.
resource "azurerm_resource_group" "rg" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_virtual_network" "vnet" {
  name                = "${var.name_prefix}-vnet-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  address_space       = [var.vnet_address_space]
}

# Subnet layout inside the /23 (offsets match the Vitally estate's, in a different range):
#   x.x.0.32/28  snet-pe          Key Vault + ACR private endpoints
#   x.x.0.64/27  snet-app         Container Apps environment (workload profiles need at least /27)
#   x.x.0.96/27  snet-pe-monitor  AMPLS private endpoint (ampls.tf) — it consumes ~13 IPs alone

# Container Apps environment infrastructure subnet (delegated)
resource "azurerm_subnet" "app" {
  name                 = "snet-app"
  resource_group_name  = azurerm_resource_group.rg.name
  virtual_network_name = azurerm_virtual_network.vnet.name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 4, 2)]

  delegation {
    name = "aca"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# Private endpoints subnet
resource "azurerm_subnet" "pe" {
  name                              = "snet-pe"
  resource_group_name               = azurerm_resource_group.rg.name
  virtual_network_name              = azurerm_virtual_network.vnet.name
  address_prefixes                  = [cidrsubnet(var.vnet_address_space, 5, 2)]
  private_endpoint_network_policies = "Disabled"
}

# NAT gateway → deterministic static egress for the env/app/job subnet. The Zendesk account can
# IP-restrict API access to this address if that is ever wanted (output `nat_egress_ip`).
resource "azurerm_public_ip" "nat" {
  name                = "${var.name_prefix}-natpip-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  allocation_method   = "Static"
  sku                 = "Standard"
}

resource "azurerm_nat_gateway" "nat" {
  name                = "${var.name_prefix}-natgw-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  sku_name            = "Standard"
}

resource "azurerm_nat_gateway_public_ip_association" "nat" {
  nat_gateway_id       = azurerm_nat_gateway.nat.id
  public_ip_address_id = azurerm_public_ip.nat.id
}

resource "azurerm_subnet_nat_gateway_association" "app" {
  subnet_id      = azurerm_subnet.app.id
  nat_gateway_id = azurerm_nat_gateway.nat.id
}

# ---- Private DNS zones (linked to this VNet only) ----
# Zones of the same names exist in the Vitally RG for its own VNet. That is fine — a private zone
# resolves only for the VNets linked to it — but it means these must stay in THIS RG and be linked
# only here.
resource "azurerm_private_dns_zone" "vault" {
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = azurerm_resource_group.rg.name
}

resource "azurerm_private_dns_zone" "acr" {
  name                = "privatelink.azurecr.io"
  resource_group_name = azurerm_resource_group.rg.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "vault" {
  name                  = "link-vault"
  resource_group_name   = azurerm_resource_group.rg.name
  private_dns_zone_name = azurerm_private_dns_zone.vault.name
  virtual_network_id    = azurerm_virtual_network.vnet.id
  registration_enabled  = false
}

resource "azurerm_private_dns_zone_virtual_network_link" "acr" {
  name                  = "link-acr"
  resource_group_name   = azurerm_resource_group.rg.name
  private_dns_zone_name = azurerm_private_dns_zone.acr.name
  virtual_network_id    = azurerm_virtual_network.vnet.id
  registration_enabled  = false
}

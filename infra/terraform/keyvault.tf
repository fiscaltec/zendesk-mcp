data "azurerm_client_config" "current" {}

# ---- Secret vault: fully private (private endpoint only) ----
#
# Holds, and is the only store for:
#   zendesk-oauth-client-secret     the Zendesk OAuth client's secret (Zendesk__OAuthClientSecretRef)
#   entra-mcp-client-secret         the Entra app's client secret — the record of what is COPIED onto
#                                   both Container Apps as `entra-oauth-client-secret`
#   zendesk-user-{objectId}         one per person connected through production, written by the app
#   zendesk-staging-user-{objectId} the same for staging (Zendesk__TokenSecretPrefix)
#
# None of these values is managed by Terraform — they would land in state, and the data plane is
# private anyway. The first two are set out-of-band with an expiry (docs/runbooks/build-the-estate.md);
# the per-person secrets are written by the app itself, which is why its identity holds Key Vault
# Secrets Officer here rather than Secrets User (identity.tf explains the trade-off).
resource "azurerm_key_vault" "secret" {
  name                          = "${var.name_prefix}-kv-uksouth"
  resource_group_name           = azurerm_resource_group.rg.name
  location                      = var.location
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = false
}

resource "azurerm_private_endpoint" "kv" {
  name                = "${var.name_prefix}-pe-kv-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  subnet_id           = azurerm_subnet.pe.id

  private_service_connection {
    name                           = "kv-conn"
    private_connection_resource_id = azurerm_key_vault.secret.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "kv-zg"
    private_dns_zone_ids = [azurerm_private_dns_zone.vault.id]
  }
}

# AuditEvent records every read and write of every secret — including each per-person token the app
# stores, reads and revokes — so this is the trail for who-touched-which-token as well as for the
# two shared secrets.
resource "azurerm_monitor_diagnostic_setting" "kv" {
  name                       = "to-law"
  target_resource_id         = azurerm_key_vault.secret.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  enabled_log { category = "AuditEvent" }
  enabled_log { category = "AzurePolicyEvaluationDetails" }
  enabled_metric { category = "AllMetrics" }
}

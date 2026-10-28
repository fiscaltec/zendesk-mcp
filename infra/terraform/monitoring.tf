# A Log Analytics workspace and Application Insights component of this estate's own. Sharing the
# Vitally estate's instead would save their fixed cost but couple two services' retention, access
# and AMPLS scope — an open decision (README.md, "Decisions pending").

resource "azurerm_log_analytics_workspace" "law" {
  name                = "${var.name_prefix}-law-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # Query is public so operators can read telemetry at all: Azure Monitor supports no IP allowlist,
  # so the alternatives are public query or none. Query is not an anonymous surface (Entra auth +
  # workspace RBAC).
  internet_query_enabled = true

  # Ingestion stays private, and nothing needs it open. Platform logs arrive by DIAGNOSTIC SETTING
  # (diagnostics.tf), which authenticates through the Azure Monitor control plane over a private
  # Microsoft channel that neither this flag nor the AMPLS modes govern. Do NOT open this to "fix"
  # missing Container Apps logs: in vitally-mcp that turned out to be an authentication problem,
  # not a network one (vitally-mcp#142) — see local_authentication_enabled below.
  internet_ingestion_enabled = false

  # No shared-key writes. This is why the Container Apps environment must use the "azure-monitor"
  # log destination (containerapps.tf): the default destination is a shared-key shipper this
  # workspace refuses. Do NOT rescue that path by enabling local auth; that trades a real hardening
  # control for the worse of two mechanisms.
  local_authentication_enabled = false
}

resource "azurerm_application_insights" "appi" {
  name                = "${var.name_prefix}-appi-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.law.id

  # The home of the audit trail. Program.cs registers the Azure Monitor exporter when
  # ApplicationInsights__ConnectionString is set (both Container Apps set it from this component),
  # and the audit records carry the microsoft.custom_event.name attribute that routes them to
  # AppEvents. AppRoleName separates the two targets, so one component serves both.
  #
  # The app's own telemetry ingests over the AMPLS private endpoint, because the app's traffic IS in
  # the VNet — so public ingestion stays off.
  internet_ingestion_enabled = false

  # Public query, for the same reason as the workspace.
  internet_query_enabled = true

  # DisableLocalAuth. Without it the connection string would be a WRITE credential for the component
  # holding the audit trail, and the risk is fabricated records: an audit trail that cannot tell
  # genuine entries from injected ones fails at the only job it has. With it, the exporter must
  # authenticate as the managed identity, which holds Monitoring Metrics Publisher here (identity.tf).
  #
  # The connection string is STILL required by the app — it names the component and its ingestion
  # endpoint; the identity replaces the authentication, not the address (vitally-mcp#147).
  local_authentication_disabled = true
}

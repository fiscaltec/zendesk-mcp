# Diagnostic settings that deliver Container Apps logs into the workspace (monitoring.tf).
#
# WHY A DIAGNOSTIC SETTING, and not the environment's own appLogsConfiguration: that is a SHARED-KEY
# shipper — it authenticates with the workspace id and primary shared key — and monitoring.tf sets
# local_authentication_enabled = false, so the workspace refuses it. In vitally-mcp it delivered not
# one row in the workspace's lifetime, and no network setting changed that (vitally-mcp#142).
#
# A diagnostic setting authenticates through the Azure Monitor control plane instead, over a private
# Microsoft channel governed by neither internet_ingestion_enabled nor the AMPLS access modes — the
# same path Key Vault and ACR use. Microsoft says so directly for this case:
#
#   "Private link: Sending logs directly to a Log Analytics Workspace through Private Link isn't
#    supported. However, you can use Azure Monitor and send your logs to the same Log Analytics
#    Workspace. This indirection is required to prevent system log data loss."
#
# ⚠️ THIS FILE IS HALF THE MECHANISM. A diagnostic setting is INERT unless the environment's
# logs_destination is "azure-monitor" (containerapps.tf). With the default "log-analytics" the
# environment keeps the shared-key shipper and ignores diagnostic settings entirely — and logs go
# nowhere, silently. Change the two together or neither.

resource "azurerm_monitor_diagnostic_setting" "cae_system_logs" {
  name                       = "cae-system-logs"
  target_resource_id         = azurerm_container_app_environment.env.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  # log_analytics_destination_type is deliberately NOT set. For this resource type the API accepts
  # "Dedicated", echoes it in the create response, and then stores null — vitally-mcp found the
  # create response untrustworthy for this resource generally and read the setting back instead.
  # Records land in the resource-specific tables (ContainerAppSystemLogs / ContainerAppConsoleLogs,
  # typed columns, no _CL suffix) regardless, so per-table retention is available.

  enabled_log {
    category = "ContainerAppSystemLogs"
  }

  # Console logs are safe to export because the application keeps customer data off stdout:
  #   * with ApplicationInsights__ConnectionString set (both apps set it — containerapps.tf), the
  #     full audit records go to AppEvents and Program.cs suppresses the AuditLogger category from
  #     the console provider, leaving only a customer-data-free breadcrumb;
  #   * Program.cs filters the System.Net.Http.HttpClient categories down to Warning, so outbound
  #     Zendesk URLs are not logged per request.
  # Those two conditions are what vitally-mcp gated enabling this category on (vitally-mcp#142/#143).
  #
  # ⚠️ It is NOT per-target. The setting is on the shared environment, so it exports the console of
  # EVERY app and job in it: staging, production and the secret-expiry scanner (whose rows carry an
  # EMPTY ContainerAppName and its name in JobName — filter on ContainerAppName alone and it
  # silently drops out). Any new app added to this environment must set
  # ApplicationInsights__ConnectionString, or its unsuppressed audit records — caller object ids, tool
  # arguments including free-text search terms, ticket and user ids — land in this table.
  enabled_log {
    category = "ContainerAppConsoleLogs"
  }

  # Startup failures reach stdout, so the console category covers them. For watching live:
  #
  #   az containerapp logs show -n zendesk-prod-ca-uksouth -g zendesk-prod-rg-uksouth \
  #     --type console --tail 100
  #
  # ⚠️ A FAILED logs show looks exactly like a quiet container. It needs
  # Microsoft.App/containerApps/getAuthToken/action, so a lapsed (PIM) elevation returns
  # AuthorizationFailed on stderr and nothing on stdout. The command above is unpiped, so its exit
  # status IS the answer — check it before concluding there were no failures.
  #
  # ContainerAppHTTPLogs is deliberately absent: it carries request URLs — including the one-time
  # /zendesk/connect and /zendesk/callback query strings and /uploads/{slot} paths — and needs PII
  # and secret-handling scrutiny before it can be considered.
}

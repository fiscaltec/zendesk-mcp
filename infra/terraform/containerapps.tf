locals {
  # The scanner Job decodes this base64'd Python script and runs it, so the job needs no image of
  # its own.
  scanner_command = "echo ${base64encode(file("${path.module}/scan/run.py"))} | base64 -d > /tmp/run.py && python3 /tmp/run.py"

  # Environment shared by BOTH Container Apps. Built once so the two targets cannot drift apart by
  # accident; containerapps-staging.tf adds only what is genuinely per-target. Values only — the
  # secret-backed and sensitive entries are separate `env` blocks in each app, because a sensitive
  # value cannot drive a dynamic block.
  app_env_shared = {
    # Zendesk. One account, one OAuth client, one vault — the targets differ only in the token
    # secret prefix and the feature switches.
    "Zendesk__Subdomain"     = var.zendesk_subdomain
    "Zendesk__KeyVaultUri"   = azurerm_key_vault.secret.vault_uri
    "Zendesk__OAuthClientId" = var.zendesk_oauth_client_id

    # DefaultAzureCredential picks the user-assigned identity by this.
    "AZURE_CLIENT_ID" = azurerm_user_assigned_identity.app.client_id

    # Entra. Audience is production's App ID URI on BOTH targets: one registration serves both
    # origins, so a staging token's `aud` names production too.
    "OAuth__Authority" = var.oauth_authority
    "OAuth__Audience"  = var.oauth_audience
    # Terminates the RFC 8707 `resource` parameter at the proxy and names the API by scope
    # instead. Required under Entra; empty is the RFC 8707 relay default, which Entra rejects.
    "OAuth__UpstreamResourceScope"        = var.oauth_upstream_resource_scope
    "OAuth__NoAuth"                       = "false"
    "OAuth__SharedClientId"               = azuread_application.zendesk_mcp.client_id
    "OAuth__AllowedClientRedirectUris__0" = var.allowed_client_redirect_uri

    # Gate 2: tiers from live, transitive sg-zendesk-* membership via Graph (needs the identity's
    # GroupMember.Read.All — identity.tf). Identity-provider independent, so shared.
    "Authorization__LiveGroupCheck" = "true"
    "Authorization__ReaderGroupId"  = azuread_group.zendesk_readers.object_id
    "Authorization__EditorGroupId"  = azuread_group.zendesk_editors.object_id
    "Authorization__AdminGroupId"   = azuread_group.zendesk_admins.object_id
  }

  app_env_production = merge(local.app_env_shared, {
    # Set explicitly although it is the application default, so the separation from staging's
    # prefix is visible here rather than implied.
    "Zendesk__TokenSecretPrefix" = "zendesk-user-"

    # Per-target: each target publishes its own origin. Resource differs from Audience by exactly
    # one trailing slash — see variables.tf; they must not be reunified.
    "OAuth__Resource"      = var.oauth_resource
    "OAuth__PublicBaseUrl" = var.public_base_url

    "Zendesk__Features__AllowPublicReplies"              = tostring(var.zendesk_allow_public_replies)
    "Zendesk__Features__AllowArticlePublishing"          = tostring(var.zendesk_allow_article_publishing)
    "Zendesk__Features__AllowMacroChanges"               = tostring(var.zendesk_allow_macro_changes)
    "Zendesk__Features__AllowUserAndOrganizationChanges" = tostring(var.zendesk_allow_user_and_organization_changes)
  })
}

resource "azurerm_container_app_environment" "env" {
  name                = "${var.name_prefix}-cae-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location

  # NOT the default "log-analytics" destination. That writes to the workspace with its SHARED KEY,
  # which monitoring.tf disables (local_authentication_enabled = false), so it would deliver nothing
  # — as it delivered nothing for the Vitally estate's whole life before vitally-mcp#142. It is also
  # unsupported with Private Link by design; Microsoft's Container Apps log-options documentation:
  #
  #   "Private link: Sending logs directly to a Log Analytics Workspace through Private Link isn't
  #    supported. However, you can use Azure Monitor and send your logs to the same Log Analytics
  #    Workspace. This indirection is required to prevent system log data loss."
  #
  # With "azure-monitor", the categories and destination come from DIAGNOSTIC SETTINGS
  # (diagnostics.tf). ⚠️ They are not optional with this value: without them logs go nowhere,
  # silently. log_analytics_workspace_id is deliberately absent — it belongs to the other destination.
  logs_destination = "azure-monitor"

  infrastructure_subnet_id       = azurerm_subnet.app.id
  internal_load_balancer_enabled = false # external ingress — the apps are internet-facing

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_container_app" "app" {
  name                         = "${var.name_prefix}-ca-uksouth"
  resource_group_name          = azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  # ONE secret, the same value and name on staging. The name records provenance deliberately: it is
  # a COPY of the Key Vault secret `entra-mcp-client-secret`, not a reference to it, so rotating the
  # vault copy alone changes nothing the app sends (vitally-mcp#138). The Zendesk OAuth client secret
  # is NOT here — the app reads it from Key Vault itself (Zendesk__OAuthClientSecretRef).
  secret {
    name  = "entra-oauth-client-secret"
    value = var.oauth_shared_client_secret
  }

  ingress {
    external_enabled = true
    target_port      = 8080 # Dockerfile: ASPNETCORE_URLS=http://+:8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # The custom domain (zendesk-mcp.fiscaltec.com) is bound to a FREE MANAGED certificate, which
    # azurerm cannot create, so the binding is done out-of-band after the first apply (README.md,
    # apply procedure).
  }

  template {
    # One replica always warm: a scale-to-zero cold start (scheduling, image pull over the private
    # endpoint, .NET boot, first Key Vault and Graph token acquisitions) cost ~5s server-side in the
    # Vitally estate, and a warm replica keeps the token, JWKS and Key Vault caches hot. The cost is
    # one 0.5 vCPU / 1Gi replica billed continuously.
    min_replicas = 1

    # ⚠️ EXACTLY ONE replica, and do not raise this on its own. Connect links, Zendesk OAuth attempt
    # state, the Entra OAuth proxy's state and upload slots (PUT /uploads/{slot}) all live in an
    # IN-PROCESS cache: a request landing on a second replica would not find the link, the callback
    # state or the slot its first request created. The per-person token cache must also stay coherent
    # with a disconnect or revocation, which one process guarantees and several do not. Scaling out
    # needs a shared cache (Redis) first — a registration change in Program.cs, then this.
    max_replicas = 1

    container {
      name   = "zendesk-mcp"
      image  = "${azurerm_container_registry.acr.login_server}/zendesk-mcp:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = local.app_env_production
        content {
          name  = env.key
          value = env.value
        }
      }

      env {
        name        = "OAuth__SharedClientSecret"
        secret_name = "entra-oauth-client-secret"
      }

      # Switches on BOTH the Azure Monitor exporter — which routes the audit records to AppEvents —
      # and the suppression of the ZendeskMcp.AuditLogger category from the console provider. Unset,
      # the full audit records stay on stdout, which diagnostics.tf exports for the whole environment.
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.appi.connection_string
      }
    }
  }

  # The first revision must be able to pull its image (AcrPull, over the private endpoint) and reach
  # the vault at startup; nothing in the attributes above implies that ordering.
  depends_on = [
    azurerm_role_assignment.mi_acr_pull,
    azurerm_role_assignment.mi_kv_secrets_officer,
    azurerm_private_endpoint.acr,
    azurerm_private_endpoint.kv,
    azurerm_private_dns_zone_virtual_network_link.acr,
    azurerm_private_dns_zone_virtual_network_link.vault,
  ]

  lifecycle {
    # deploy.yml owns the image after creation. Without this, every apply would roll production back
    # to var.image_tag.
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition     = var.oauth_shared_client_secret != null && var.oauth_shared_client_secret != ""
      error_message = "oauth_shared_client_secret is required to create or update the Container Apps. Create the Entra client secret first (README.md, apply procedure step 2) and supply it as TF_VAR_oauth_shared_client_secret."
    }
  }
}

# Scheduled secret-expiry scanner: alerts Teams about secrets in the secret vault nearing expiry.
# It skips the per-person Zendesk token secrets — see scan/run.py.
resource "azurerm_container_app_job" "scanner" {
  name                         = "${var.name_prefix}-secscan-uksouth"
  resource_group_name          = azurerm_resource_group.rg.name
  location                     = var.location
  container_app_environment_id = azurerm_container_app_environment.env.id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 1800
  replica_retry_limit          = 1

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  schedule_trigger_config {
    cron_expression          = "0 8 * * 1"
    parallelism              = 1
    replica_completion_count = 1
  }

  secret {
    name  = "teams-webhook"
    value = var.teams_webhook_url
  }

  template {
    container {
      name    = "secretscan"
      image   = "python:3-slim"
      cpu     = 0.5
      memory  = "1Gi"
      command = ["/bin/sh"]
      args    = ["-c", local.scanner_command]

      env {
        name  = "MI_CLIENT_ID"
        value = azurerm_user_assigned_identity.app.client_id
      }
      env {
        name  = "VAULT"
        value = azurerm_key_vault.secret.name
      }
      env {
        name        = "TEAMS_WEBHOOK"
        secret_name = "teams-webhook"
      }
    }
  }
}

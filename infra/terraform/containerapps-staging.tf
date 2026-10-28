# Staging Container App — the pre-production target. The release train deploys every tag here
# before production (release.yml), and changes to authentication or to the per-person Zendesk
# connection — the largest blast radius in this system — are validated here first.
#
# WHY IT IS CHEAP. It is a second app in the same VNet-injected Container Apps Environment, so it
# reaches Key Vault and ACR over the existing private endpoints with no extra networking, and it
# shares the managed identity, so every grant (AcrPull, Key Vault, Graph GroupMember.Read.All)
# already applies. It scales to zero. What is staging-specific is this app, a stable hostname with
# its certificate, one federated credential (identity.tf) and one redirect URI on each OAuth client.
#
# WHAT IT SHARES, AND WHAT KEEPS IT APART. There is one Zendesk account, one Zendesk OAuth client and
# one vault. Two settings keep staging from touching production's state:
#   * Zendesk__TokenSecretPrefix = zendesk-staging-user-. A person connects to each target
#     separately, and the tokens are stored under different names, so a person using staging is
#     never acting through a token production stored, and a staging defect in token handling cannot
#     corrupt or revoke a production token, though both live in one vault.
#   * Authorization__ReadOnly (var.staging_read_only, default true). Staging runs unreleased code
#     against the live Zendesk account; every Zendesk call runs as the signed-in person, so a write
#     made through staging is a real change to a real ticket, article or macro.
resource "azurerm_container_app" "staging" {
  name                         = var.staging_app_name
  resource_group_name          = azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = data.azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  # The SAME value under the SAME name as production: one Entra app registration serves both
  # targets. A wrong secret is not caught at startup — the app boots and /health passes — and
  # surfaces only at the token exchange, so a secret change is verified by signing in.
  secret {
    name  = "entra-oauth-client-secret"
    value = var.oauth_shared_client_secret
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # zendesk-mcp-staging.fiscaltec.com, bound to a free managed certificate out-of-band, as
    # production is (README.md, apply procedure).
  }

  template {
    # Scale to zero: the warm replica production keeps protects real users from a cold start, and
    # staging has none. A cold start is absorbed by the retries in deploy.yml's smoke and
    # verify-oauth-metadata.sh — but a connect link or upload slot does not survive a scale-in, so
    # a person testing the connect flow should finish it in one sitting.
    min_replicas = 0
    # One, for the same reason as production: in-process connect links, OAuth state and upload
    # slots (containerapps.tf).
    max_replicas = 1

    container {
      name   = "zendesk-mcp"
      image  = "${data.azurerm_container_registry.acr.login_server}/zendesk-mcp:${var.staging_image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = merge(local.app_env_shared, {
          "Zendesk__TokenSecretPrefix" = "zendesk-staging-user-"

          # NOT production's: each target publishes its own origin, and strict RFC 9728 clients
          # reject a document whose resource names another server.
          "OAuth__Resource"      = var.staging_oauth_resource
          "OAuth__PublicBaseUrl" = var.staging_public_base_url

          "Authorization__ReadOnly" = tostring(var.staging_read_only)

          "Zendesk__Features__AllowPublicReplies"              = tostring(var.staging_zendesk_allow_public_replies)
          "Zendesk__Features__AllowArticlePublishing"          = tostring(var.staging_zendesk_allow_article_publishing)
          "Zendesk__Features__AllowHelpCenterStructureChanges" = tostring(var.staging_zendesk_allow_help_center_structure_changes)
          "Zendesk__Features__AllowMacroChanges"               = tostring(var.staging_zendesk_allow_macro_changes)
          "Zendesk__Features__AllowUserAndOrganizationChanges" = tostring(var.staging_zendesk_allow_user_and_organization_changes)
        })
        content {
          name  = env.key
          value = env.value
        }
      }

      env {
        name        = "OAuth__SharedClientSecret"
        secret_name = "entra-oauth-client-secret"
      }

      # Required here as much as on production, and for a reason beyond this app: console logs are
      # exported for the WHOLE environment (diagnostics.tf), so a staging app without it carries its
      # unsuppressed audit records into the shared console table.
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.appi.connection_string
      }
    }
  }

  # As production: the first revision must be able to pull its image and reach the vault.
  depends_on = [
    azurerm_role_assignment.mi_acr_pull,
    azurerm_role_assignment.mi_kv_secrets_officer,
    azurerm_private_endpoint.acr,
    azurerm_private_endpoint.kv,
    azurerm_private_dns_zone_virtual_network_link.acr,
    azurerm_private_dns_zone_virtual_network_link.vault,
  ]

  lifecycle {
    # deploy.yml owns the image after creation.
    ignore_changes = [template[0].container[0].image]

    precondition {
      condition     = var.oauth_shared_client_secret != null && var.oauth_shared_client_secret != ""
      error_message = "oauth_shared_client_secret is required to create or update the Container Apps. Create the Entra client secret first (README.md, apply procedure step 2) and supply it as TF_VAR_oauth_shared_client_secret."
    }
  }
}

# Checking what staging is actually SERVING, rather than what its template says (an accepted update
# reports the new template while the previous revision may still take requests):
#
#   bash .github/scripts/check-serving-revisions.sh zendesk-staging-ca-uksouth zendesk-prod-rg-uksouth \
#     --env Authorization__ReadOnly --equals true --env ApplicationInsights__ConnectionString \
#     --env Zendesk__TokenSecretPrefix --equals zendesk-staging-user-
#
# Exit 0 is the only pass; 2 (NOT ASSESSED) means no verdict was possible (vitally-mcp#167).

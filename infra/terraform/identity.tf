# User-assigned managed identity: the runtime identity of both Container Apps and the scanner Job,
# and the GitHub Actions deploy identity (federated credentials below).
resource "azurerm_user_assigned_identity" "app" {
  name                = "${var.name_prefix}-id-uksouth"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.location
}

# Runtime: read AND WRITE secrets in the secret vault.
#
# Secrets Officer, not Secrets User, because the app writes: it stores one secret per connected
# person (zendesk-user-{objectId}, or zendesk-staging-user-{objectId}), overwrites it on every token
# refresh and tombstones it on disconnect. Secrets User would let people connect and then fail every
# save.
#
# The cost, stated plainly: the same grant lets the app overwrite or delete the two shared secrets in
# this vault — `zendesk-oauth-client-secret` and `entra-mcp-client-secret` — and the scanner Job, which
# shares the identity, could too. Accepted, with one vault, because:
#   * the identity already holds every person's Zendesk token, each of which can act in Zendesk as
#     that person. A compromise of the identity is already the worst case; reaching the Zendesk
#     client secret adds little to it.
#   * a second vault for the two shared secrets would need its own private endpoint, diagnostic
#     setting, expiry scanning and rotation steps, while the app still needs read on it — more
#     surface to keep right, for no reduction in what the identity can do to Zendesk.
#   * the app only supports one Zendesk__KeyVaultUri, so a split would also be a code change.
# The mitigations are the vault's AuditEvent log (keyvault.tf), soft delete with purge protection
# (any overwrite or delete is recoverable for 90 days), and the Container App's `entra-oauth-client-
# secret` being a COPY — the app never reads `entra-mcp-client-secret`, so tampering with it changes
# nothing the app sends.
resource "azurerm_role_assignment" "mi_kv_secrets_officer" {
  scope                = azurerm_key_vault.secret.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Runtime: pull the container image from the shared registry (acr.tf). AcrPull is registry-wide, so
# this also reads Vitally's images; reading them is not the risk — the import grant below is.
resource "azurerm_role_assignment" "mi_acr_pull" {
  scope                = data.azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Telemetry: publish audit records and traces to Application Insights. Required, not optional: the
# component has DisableLocalAuth = true (monitoring.tf), so the instrumentation key in the connection
# string is NOT accepted as a credential and the exporter authenticates with this identity. Without
# this grant the records are dropped at ingestion — asynchronously, so nothing in the app notices
# (vitally-mcp#164/#147).
#
# ⚠️ Verify DisableLocalAuth with `az resource show`, not `az monitor app-insights component show`:
# the extension has been seen to report it as null while ARM reports true.
resource "azurerm_role_assignment" "mi_monitoring_metrics_publisher" {
  scope                = azurerm_application_insights.appi.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# NOTE (Microsoft Graph): the identity ALSO needs the Graph APPLICATION permission
# GroupMember.Read.All, for the live sg-zendesk-* membership check (Gate 2). Without it every tool
# call is denied — the resolver never fails open. It is not granted here: an app-role grant on
# Microsoft Graph needs a Privileged Role Administrator–level principal, which the identity running
# routine applies should not hold. Grant it once, out-of-band (docs/runbooks/build-the-estate.md), to the
# identity's principal id (output `managed_identity_principal_id`).

# CI/CD (GitHub Actions OIDC): GitHub authenticates as this identity to deploy. The federated
# credential subject matches the deploy workflow's `environment:` job, so only that environment can
# mint a token. Audience is the value azure/login requests by default.
#
# ONE CREDENTIAL PER TARGET, and that is the whole reason deploy.yml keys off a GitHub environment
# name: the subject is an exact string match, so a deploy to a target with no credential here fails
# at azure/login rather than deploying somewhere unintended.
resource "azurerm_federated_identity_credential" "github_actions_prod" {
  name                = "github-actions-prod-env"
  resource_group_name = azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.app.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:fiscaltec/zendesk-mcp:environment:production"
}

resource "azurerm_federated_identity_credential" "github_actions_staging" {
  name                = "github-actions-staging-env"
  resource_group_name = azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.app.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:fiscaltec/zendesk-mcp:environment:staging"
}

# Deploy: `az acr import` the built image into the private ACR. AcrPush does NOT include the
# importImage action, and built-in Contributor would also let this identity delete or reconfigure the
# registry — which is the Vitally estate's (acr.tf). So a custom role grants exactly what the import
# needs: read the registry, and trigger an import. Scoped to that one registry.
#
# ⚠️ What remains: an import with --force can still overwrite any tag in the shared registry,
# Vitally's included (acr.tf). Creating this role and assignment needs whoever builds it to
# hold role-administration rights over the Vitally estate's registry.
resource "azurerm_role_definition" "acr_importer" {
  name        = "Zendesk MCP ACR importer"
  scope       = data.azurerm_container_registry.acr.id
  description = "Import images into the shared registry (az acr import) and nothing else. Assigned to the Zendesk MCP identity."

  permissions {
    actions = [
      "Microsoft.ContainerRegistry/registries/read",
      "Microsoft.ContainerRegistry/registries/importImage/action",
    ]
  }

  assignable_scopes = [data.azurerm_container_registry.acr.id]
}

resource "azurerm_role_assignment" "mi_acr_importer" {
  scope              = data.azurerm_container_registry.acr.id
  role_definition_id = azurerm_role_definition.acr_importer.role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.app.principal_id
}

# Deploy: roll the Container App to the new revision (`az containerapp update`).
resource "azurerm_role_assignment" "mi_ca_contributor" {
  scope                = azurerm_container_app.app.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Deploy: the same, for staging. Scoped to the app rather than the resource group so a deploy to one
# target cannot roll the other.
resource "azurerm_role_assignment" "mi_ca_staging_contributor" {
  scope                = azurerm_container_app.staging.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Release train: tell an ABSENT staging apart from a failed lookup (vitally-mcp#171). With only the
# app-scoped Contributor above, Azure answers `AuthorizationFailed` for an app that does not exist —
# the same answer as a lost role — and `az containerapp list` silently lists only what the caller can
# see, so absence is unobservable. With this grant a missing app reads `(ResourceNotFound)`, which
# check-serving-revisions.sh reports as exit 3 and staging-gate.yml turns into "skip staging".
#
# Mostly read, and not nothing: the identity is also both apps' RUNTIME identity, so the running
# containers can read every Container App's configuration in the group (non-secret env values
# included; secrets are a separate `listSecrets` action this role does not grant), every role
# assignment in it (`Microsoft.Authorization/*/read`) and its deployment history. The built-in role
# also carries `Microsoft.Insights/alertRules/*` — WRITE on classic alert rules. Accepted over
# maintaining a custom role, as it was for the Vitally estate.
resource "azurerm_role_assignment" "mi_rg_containerapp_reader" {
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "ContainerApp Reader"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

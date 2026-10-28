# Entra app registration for the Zendesk MCP server — the OAuth client *and* the API resource in one
# registration, serving both targets. The design is vitally-mcp#107's; the lessons cited below were
# learned there.
#
# The principal running Terraform needs, in Entra: Application Administrator (or
# Application.ReadWrite.OwnedBy), Groups Administrator for the sg-zendesk-* groups, and the right to
# assign the app's default role to groups (Gate 1). README.md, "Prerequisites".
#
# ---------------------------------------------------------------------------------------------
# TWO ORDERING CONSTRAINTS (both hit for real in vitally-mcp). Terraform hides the first and NOT
# the second, so read this before editing:
#
#  1. `api.requestedAccessTokenVersion = 2` must be set BEFORE `identifier_uris`. The tenant's
#     `defaultAppManagementPolicy` enables
#     `identifierUris.uriAdditionWithoutUniqueTenantIdentifier`, which would force the
#     `https://fiscaltec.com/{guid}` shape; an app is exempt only via `excludeAppsReceivingV2Tokens`.
#     The azuread provider sets both in one create call with the version first, so this works —
#     but a manual recreate must do it in two steps.
#
#  2. A scope cannot be referenced by `pre_authorized_application` in the same write that creates
#     it: Graph rejects the whole request with
#     `InvalidValue … has a Permission Id that cannot be found in the AppPermissions sets`.
#     Terraform models this correctly *because* `azuread_application_pre_authorized` is a separate
#     resource — keep it separate rather than folding it into an `api {}` block.
# ---------------------------------------------------------------------------------------------

data "azuread_client_config" "current" {}

# Microsoft Graph, for the delegated OIDC scopes below.
data "azuread_service_principal" "msgraph" {
  client_id = "00000003-0000-0000-c000-000000000000"
}

# The department groups that gate sign-in (Gate 1), by object id rather than display name so they
# are provably the intended groups and not merely same-named ones.
#
# Decided 2026-10-28: only the IT & Security Department to begin with (the Vitally MCP server admits
# nine; they are not assumed here). Its object id is the one vitally-mcp's entra.tf assigns. Add a
# department by adding its group here AND nesting it into a sg-zendesk-* tier group (ACCESS.md).
#
# ⚠️ This map is the ONLY record of who can sign in, and an omission is an outage for that
# department at its next sign-in (AADSTS50105). vitally-mcp's list drifted twice — both times a
# department onboarded per the access procedure was missing here — so the procedure people follow
# (ACCESS.md) must name this map and this app.
variable "entra_gate1_group_object_ids" {
  type        = map(string)
  description = "Department groups assigned directly to the Zendesk MCP app for the sign-in gate, keyed by display name. The sole record of who can sign in."
  default = {
    "IT & Security Department" = "6ba9bf61-e959-4dd9-8a96-d477a35b0d03"
  }
}

variable "entra_mcp_access_scope_id" {
  type        = string
  description = "Stable id of the exposed mcp.access delegated scope. Changing it after the first apply invalidates every consent grant referencing it."
  default     = "c46bfcbe-1e71-4068-a6a8-6401ea221f85"
}

resource "azuread_application" "zendesk_mcp" {
  display_name     = "Zendesk MCP"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]

  notes = "OAuth client + API resource for the Zendesk MCP server (github.com/fiscaltec/zendesk-mcp). Managed by infra/terraform/entra.tf."

  # No trailing slash — Entra refuses to register one (`IdentifierUrisEndsWithSlash` /
  # `ValueCannotEndWithSlash`). Clients still send the trailing-slash form as their RFC 8707
  # `resource`; OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference
  # so the two forms name one resource.
  identifier_uris = [var.oauth_audience]

  api {
    # See ordering constraint 1 in the header.
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = var.entra_mcp_access_scope_id
      value                      = "mcp.access"
      type                       = "User"
      enabled                    = true
      admin_consent_display_name = "Access the Zendesk MCP server"
      admin_consent_description  = "Allows the signed-in user to call the Zendesk MCP server on their behalf. Tool-level entitlement is resolved separately from sg-zendesk-* group membership, and every Zendesk call runs as the user's own connected Zendesk account."
      user_consent_display_name  = "Access Zendesk MCP"
      user_consent_description   = "Allows you to use the Zendesk MCP server on your behalf."
    }
  }

  # `upn` on the access token: CallerIdentity.TryGetSignInName prefers it to `preferred_username`
  # (which Microsoft documents as mutable and not for authorisation) when matching a person's Zendesk
  # login to their Entra identity at /zendesk/callback.
  optional_claims {
    access_token {
      name = "upn"
    }
  }

  # The proxy owns a single fixed callback per origin; MCP clients never register with Entra —
  # they converge on this app through the RFC 7591 DCR shim at /oauth/register. (The Zendesk
  # connection's /zendesk/callback is a redirect URL on the ZENDESK OAuth client, not here.)
  web {
    redirect_uris = [
      "${var.public_base_url}/oauth/callback",
      "${var.staging_public_base_url}/oauth/callback",
    ]

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  # API permissions — Graph's OIDC scopes and this app's own mcp.access — are the separate
  # azuread_application_api_access resources below. The self-reference cannot be written inline (a
  # resource may not refer to its own attributes), and the two forms must not be mixed, hence the
  # ignore_changes on required_resource_access.

  # If the hostnames ever change, the OAuth origin variables must be edited together. These refuse a
  # combination that would register one thing and serve another.
  lifecycle {
    ignore_changes = [required_resource_access]

    precondition {
      condition     = !endswith(var.oauth_audience, "/") && var.oauth_audience == var.public_base_url
      error_message = "oauth_audience must equal public_base_url, with no trailing slash (Entra refuses one on identifier URIs)."
    }
    precondition {
      condition     = var.oauth_resource == "${var.public_base_url}/" && var.staging_oauth_resource == "${var.staging_public_base_url}/"
      error_message = "Each target's oauth resource must be its public origin plus exactly one trailing slash."
    }
    precondition {
      condition     = var.oauth_upstream_resource_scope == "${var.oauth_audience}/mcp.access"
      error_message = "oauth_upstream_resource_scope must be the App ID URI (oauth_audience) followed by /mcp.access."
    }
  }
}

# Delegated only. The server never calls Graph as the user — entitlement comes from the Container
# Apps' managed identity holding GroupMember.Read.All (identity.tf), unrelated to this registration.
resource "azuread_application_api_access" "msgraph" {
  application_id = azuread_application.zendesk_mcp.id
  api_client_id  = data.azuread_service_principal.msgraph.client_id

  # Ids resolved from this tenant's Graph service principal rather than copied from documentation:
  # `email` is 64a6cdd6-…-3cc8405e90d0 here, which is *not* the value that commonly circulates for it.
  scope_ids = [
    "37f7f235-527c-4136-accd-4a02d197296e", # openid
    "14dad69e-099b-42c9-810b-d002981feec1", # profile
    "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0", # email
    "7427e0e9-2fba-42fe-b0c0-848c9e6a8182", # offline_access
  ]
}

# The app requests its own API's scope: that request is what binds the access token's audience to
# this resource, because the proxy terminates the RFC 8707 `resource` parameter instead of relaying
# it (oauth_upstream_resource_scope). Being a separate write from the one that creates the scope
# also satisfies ordering constraint 2.
resource "azuread_application_api_access" "self" {
  application_id = azuread_application.zendesk_mcp.id
  api_client_id  = azuread_application.zendesk_mcp.client_id
  scope_ids      = [var.entra_mcp_access_scope_id]
}

# Pre-authorising the app for its own scope is what suppresses the per-user consent screen, together
# with tenant-wide admin consent. Note this is NOT what authenticates the token exchange — that is
# the injected client secret — and conflating the two invites removing the wrong setting.
# Separate resource by necessity: see ordering constraint 2 in the header.
resource "azuread_application_pre_authorized" "self" {
  application_id       = azuread_application.zendesk_mcp.id
  authorized_client_id = azuread_application.zendesk_mcp.client_id
  permission_ids       = [var.entra_mcp_access_scope_id]
}

resource "azuread_service_principal" "zendesk_mcp" {
  client_id = azuread_application.zendesk_mcp.client_id
  owners    = [data.azuread_client_config.current.object_id]

  # Gate 1 — sign-in is restricted to principals assigned below, for both targets.
  app_role_assignment_required = true
}

# Gate 1 assignments. **These must be direct.** The Entra app-assignment gate honours only direct
# members of an assigned group — nesting does not grant sign-in. So the department groups are
# assigned here individually; assigning the sg-zendesk-* tier groups instead would admit only their
# direct members. vitally-mcp hit exactly that trap, which is why department groups are both
# assigned here AND nested inside the tier groups for Gate 2.
#
# Gate 2 (which tier of tools a signed-in user gets) is a different mechanism entirely:
# GraphGroupPermissionResolver reads sg-zendesk-* membership *transitively* via Graph, using only
# the `oid` claim, so it is independent of this registration.
resource "azuread_app_role_assignment" "gate1" {
  for_each = var.entra_gate1_group_object_ids

  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.zendesk_mcp.object_id

  # The all-zero id is the implicit "default access" role, which is what an app with no declared
  # app roles assigns. This registration deliberately declares none: entitlement is resolved from
  # Graph group membership, not from roles or a groups claim.
  app_role_id = "00000000-0000-0000-0000-000000000000"
}

# ---- Gate 2: the permission tiers ----
#
# Created here; MEMBERSHIP IS NOT. `members` is deliberately absent, which tells the provider not to
# manage it, so admins can grant and revoke tiers without a Terraform run (ACCESS.md). The usual
# shape, as in vitally-mcp, is to nest each department group into one tier group, and to add
# individuals only for exceptions. Tiers are cumulative in the resolver: an editor need not also be
# a reader. Membership is read live (Authorization__LiveGroupCheck), so a revocation bites within
# the cache window rather than at token expiry.
#
# Security groups, not mail-enabled, not role-assignable: nothing but the resolver reads them.
resource "azuread_group" "zendesk_readers" {
  display_name            = "sg-zendesk-readers"
  description             = "Zendesk MCP server: read tools. Membership read transitively by the server (Gate 2); sign-in is Gate 1, on the app."
  security_enabled        = true
  mail_enabled            = false
  prevent_duplicate_names = true
  owners                  = [data.azuread_client_config.current.object_id]
}

resource "azuread_group" "zendesk_editors" {
  display_name            = "sg-zendesk-editors"
  description             = "Zendesk MCP server: read and create/update tools."
  security_enabled        = true
  mail_enabled            = false
  prevent_duplicate_names = true
  owners                  = [data.azuread_client_config.current.object_id]
}

resource "azuread_group" "zendesk_admins" {
  display_name            = "sg-zendesk-admins"
  description             = "Zendesk MCP server: every tool, including deletes."
  security_enabled        = true
  mail_enabled            = false
  prevent_duplicate_names = true
  owners                  = [data.azuread_client_config.current.object_id]
}

# ---------------------------------------------------------------------------------------------
# NOT managed here, deliberately:
#
#  * The client secret. Create it out-of-band after the first apply and store it in Key Vault as
#    `entra-mcp-client-secret` — see docs/runbooks/entra-and-zendesk-oauth.md. Modelling it as
#    `azuread_application_password` would persist the value in state AND tie its rotation to a
#    Terraform run, and the vault's data plane is private-endpoint only so Terraform could not
#    store it there from outside the VNet anyway.
#
#  * The tenant-wide admin consent grant (`openid profile email offline_access mcp.access`).
#    `azuread_service_principal_delegated_permission_grant` could express it, but its drift
#    detection is noisy for no operational gain; it is one `az ad app permission admin-consent`
#    (docs/runbooks/build-the-estate.md).
#
#  * Graph `GroupMember.Read.All` for the managed identity — see identity.tf.
# ---------------------------------------------------------------------------------------------

output "entra_app_client_id" {
  description = "appId of the Zendesk MCP Entra app — OAuth:SharedClientId on both targets."
  value       = azuread_application.zendesk_mcp.client_id
}

output "entra_app_object_id" {
  description = "Object id of the Zendesk MCP app registration (not the appId) — what `az ad app credential` commands and docs/runbooks/rotate-helpers.sh take."
  value       = azuread_application.zendesk_mcp.object_id
}

output "entra_app_id_uri" {
  description = "App ID URI — OAuth:Audience on both targets. Note the ABSENCE of a trailing slash, unlike OAuth:Resource."
  value       = one(azuread_application.zendesk_mcp.identifier_uris)
}

output "entra_group_reader_id" {
  description = "Object id of sg-zendesk-readers (Authorization:ReaderGroupId)."
  value       = azuread_group.zendesk_readers.object_id
}

output "entra_group_editor_id" {
  description = "Object id of sg-zendesk-editors (Authorization:EditorGroupId)."
  value       = azuread_group.zendesk_editors.object_id
}

output "entra_group_admin_id" {
  description = "Object id of sg-zendesk-admins (Authorization:AdminGroupId)."
  value       = azuread_group.zendesk_admins.object_id
}

# Inputs. A value nobody has decided yet is a variable WITHOUT a default, so Terraform stops and asks
# rather than applying a guess; each one is listed under "Decisions pending" in README.md.

# ---- Placement ----
variable "subscription_id" {
  type        = string
  description = "Azure subscription (IT-Production), shared with the Vitally MCP estate."
  default     = "282207c6-4107-47fa-9d4e-b2fa9b3066cb"
}

variable "tenant_id" {
  type        = string
  description = "Entra tenant hosting the app registration, the sg-zendesk-* tier groups and the department groups."
  default     = "75bd6050-92a8-4bde-a406-50000b310c86"
}

variable "location" {
  type    = string
  default = "uksouth"
}

variable "resource_group_name" {
  type        = string
  description = "Resource group this configuration CREATES and owns. Everything in the estate lives in it."
  default     = "zendesk-prod-rg-uksouth"
}

variable "name_prefix" {
  type        = string
  description = "Resource naming prefix ({prefix}-{type}-uksouth convention)."
  default     = "zendesk-prod"

  # Key Vault names are at most 24 characters, and "-cmk-uksouth" is 12 of them.
  validation {
    condition     = length(var.name_prefix) <= 12 && can(regex("^[a-z][a-z0-9-]*$", var.name_prefix))
    error_message = "name_prefix must be lower-case, start with a letter and be at most 12 characters, or the Key Vault names exceed 24."
  }
}

# Globally unique, alphanumeric-only names, so they cannot follow name_prefix.
variable "acr_name" {
  type    = string
  default = "zendeskproducruksouth"
}

variable "flow_log_storage_account_name" {
  type    = string
  default = "zendeskprodflowuksouth"
}

variable "vnet_address_space" {
  type        = string
  description = "VNet range (/23). Deliberately NOT Vitally's 10.80.0.0/23, so the two estates can be peered or routed through a hub later without renumbering. Subnets are carved from it in network.tf and ampls.tf."
  default     = "10.81.0.0/23"
}

# ---- Images ----
# Used only when each Container App is CREATED. After that deploy.yml owns the image and the apps
# ignore it (lifecycle.ignore_changes), so a later apply never rolls back a release. No default:
# it must name the tag imported into the ACR during the first apply (README.md, step 3).
variable "image_tag" {
  type        = string
  description = "Image tag in the ACR the production Container App is created with (initial create only)."
}

variable "staging_image_tag" {
  type        = string
  description = "Image tag in the ACR the staging Container App is created with (initial create only)."
}

# ---- Zendesk ----
variable "zendesk_subdomain" {
  type        = string
  description = "Zendesk account subdomain: `fiscaltec` for https://fiscaltec.zendesk.com. Shared by both targets — there is one Zendesk account."

  validation {
    condition     = can(regex("^[A-Za-z0-9-]+$", var.zendesk_subdomain))
    error_message = "zendesk_subdomain is the bare subdomain: letters, digits and hyphens only, no scheme or domain."
  }
}

variable "zendesk_oauth_client_id" {
  type        = string
  description = "Unique identifier of the Zendesk OAuth client (Admin Centre > Apps and integrations > APIs > OAuth clients). Shared by both targets; its redirect URLs must list BOTH /zendesk/callback URLs. Its secret goes into Key Vault as `zendesk-oauth-client-secret`, never here."
}

# Account-wide safety switches (ZendeskFeatureOptions). Each guards an action that reaches beyond the
# agent making it — a customer's inbox, the public Help Center, every agent's macro list, who can see
# an organisation's tickets — and stays off until someone decides otherwise. Per target, so staging
# can trial one without production following.
variable "zendesk_allow_public_replies" {
  type        = bool
  description = "Production: allow public ticket comments, CCs and public macro replies (they email customers)."
  default     = false
}

variable "zendesk_allow_article_publishing" {
  type        = bool
  description = "Production: allow publishing Help Center articles and translations rather than saving drafts."
  default     = false
}

variable "zendesk_allow_help_center_structure_changes" {
  type        = bool
  description = "Production: allow creating and changing Help Center categories and sections, which are public as soon as they exist."
  default     = false
}

variable "zendesk_allow_macro_changes" {
  type        = bool
  description = "Production: allow creating, editing, activating and deactivating macros."
  default     = false
}

variable "zendesk_allow_user_and_organization_changes" {
  type        = bool
  description = "Production: allow new end users from an email, and organisation membership changes."
  default     = false
}

variable "staging_zendesk_allow_public_replies" {
  type    = bool
  default = false
}

variable "staging_zendesk_allow_article_publishing" {
  type    = bool
  default = false
}

variable "staging_zendesk_allow_help_center_structure_changes" {
  type    = bool
  default = false
}

variable "staging_zendesk_allow_macro_changes" {
  type    = bool
  default = false
}

variable "staging_zendesk_allow_user_and_organization_changes" {
  type    = bool
  default = false
}

variable "staging_read_only" {
  type        = bool
  description = "Authorization__ReadOnly on staging. Staging runs unreleased code against the ONE live Zendesk account, so every mutating tool is denied there unless someone deliberately turns this off for a write test (and back on afterwards)."
  default     = true
}

# ---- OAuth (Entra) ----
#
# Shared by production and staging except `*_resource` and `*_public_base_url`, which stay per-target
# permanently: each target publishes its own origin, and sharing those would make staging advertise
# production's — an RFC 9728 document naming a server it is not, which strict clients reject.
#
# The hostnames below are a PROPOSAL (README.md, "Decisions pending"). The Entra app registration is
# built from these same variables (entra.tf), and preconditions there refuse a combination that does
# not hang together.
variable "oauth_authority" {
  type        = string
  description = "Upstream OIDC issuer — Entra, shared by both targets. Endpoints are read from the issuer's discovery document, not built from this."
  default     = "https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0"
}

# oauth_audience and oauth_resource are NOT the same value and must not be reconciled. They differ
# by exactly one trailing slash (vitally-mcp#108):
#
#   Audience follows the Entra App ID URI, which cannot carry a trailing slash — Entra refuses to
#     register one on identifierUris.
#   Resource is published in the RFC 9728 document and keeps the slash, because that is the form
#     Claude Code normalises to and then compares.
#
# OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference, which is what
# lets the two forms name one resource. Reconciling them breaks token validation in one direction
# and the metadata document in the other.
variable "oauth_audience" {
  type        = string
  description = "JWT aud — the Entra App ID URI, with NO trailing slash. Shared: one registration serves both origins, so a staging token's aud names this production URI too. Also the identifier URI entra.tf registers."
  default     = "https://zendesk-mcp.fiscaltec.com"
}

variable "oauth_resource" {
  type        = string
  description = "Canonical resource identifier PRODUCTION publishes in RFC 9728 and validates RFC 8707 indicators against. WITH the trailing slash. Per-target."
  default     = "https://zendesk-mcp.fiscaltec.com/"
}

variable "oauth_upstream_resource_scope" {
  type        = string
  description = "Shared. Set, so the proxy terminates the RFC 8707 `resource` parameter instead of relaying it — required under Entra, whose v2 authorize endpoint refuses any `resource` that does not match the requested scopes (AADSTS9010010). Must be the App ID URI plus /mcp.access."
  default     = "https://zendesk-mcp.fiscaltec.com/mcp.access"
}

variable "public_base_url" {
  type        = string
  description = "Canonical public origin for PRODUCTION — no trailing slash. Per-target."
  default     = "https://zendesk-mcp.fiscaltec.com"
}

variable "staging_app_name" {
  type        = string
  description = "Staging Container App name. Outside the name_prefix convention: it is a second app inside the production RG and Container Apps Environment, not a second environment."
  default     = "zendesk-staging-ca-uksouth"
}

# Staging's Audience and Resource diverge by HOST as well as by slash, and that is expected: one
# Entra registration serves both origins, so a staging token's `aud` is production's App ID URI,
# whereas `resource` must equal the staging origin, because MCP clients reject a metadata document
# whose `resource` does not name the server they fetched it from.
variable "staging_oauth_resource" {
  type        = string
  description = "Canonical resource identifier published by staging, WITH the trailing slash. Must equal the staging origin plus '/'."
  default     = "https://zendesk-mcp-staging.fiscaltec.com/"
}

variable "staging_public_base_url" {
  type        = string
  description = "Canonical public origin for staging — no trailing slash."
  default     = "https://zendesk-mcp-staging.fiscaltec.com"
}

variable "allowed_client_redirect_uri" {
  type    = string
  default = "https://claude.ai/api/mcp/auth_callback"
}

# ---- Secrets (never committed — supply via TF_VAR_* or an untracked tfvars) ----
#
# ONE Entra client secret for both targets, held on each Container App under the same name,
# `entra-oauth-client-secret`. It is a COPY of the Key Vault secret `entra-mcp-client-secret`, not a
# reference to it — so rotating the vault copy alone changes nothing the app sends (vitally-mcp#138's
# trap). Rotation: docs/runbooks/entra-and-zendesk-oauth.md.
#
# Null by default ONLY so the first, targeted apply can create the Entra app before this secret can
# exist (README.md, apply procedure). Both Container Apps carry a precondition that refuses to plan
# without it, so a full apply cannot run on the default.
#
# The hazard is a WRONG value, not a missing one: a stale secret boots fine, passes /health and the
# unauthenticated 401, and fails only at /oauth/token as `invalid_client` — for everyone at once.
# Read this secret's line in the plan, and verify a change by signing in.
variable "oauth_shared_client_secret" {
  type        = string
  description = "Client secret of the Zendesk MCP Entra app (Key Vault `entra-mcp-client-secret`), copied onto BOTH Container Apps as 'entra-oauth-client-secret'."
  sensitive   = true
  nullable    = true
  default     = null
}

variable "teams_webhook_url" {
  type        = string
  description = "Teams Power Automate Workflows webhook URL for the secret-expiry scanner job."
  sensitive   = true
}

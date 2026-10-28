# Zendesk MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that gives MCP clients — **Claude Desktop**, **claude.ai**, **Claude Code**, **VS Code**, **Cursor** — access to FISCAL's [Zendesk](https://www.zendesk.com) Support and Help Center.

It is built to the same standard as FISCAL's [Vitally MCP server](https://github.com/fiscaltec/vitally-mcp): C# on .NET 10 and the official `ModelContextProtocol` SDK, hosted as a **remote HTTP MCP server** behind Microsoft Entra (OAuth 2.1 / RFC 9728), with per-tier tool permissions, a per-user audit trail and an Azure Container Apps deployment. Its tool set covers the functionality of Swifteq's hosted Zendesk MCP server — tickets, conversations, SLA and KPI reporting, CSAT, macros, users and organisations, configuration and the Help Center.

[![CI](https://github.com/fiscaltec/zendesk-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/fiscaltec/zendesk-mcp/actions/workflows/ci.yml)

> **Status: built, not yet deployed.** The Azure estate is built with `az` CLI by [`docs/runbooks/build-the-estate.md`](docs/runbooks/build-the-estate.md), as the Vitally estate is, and has not been built yet; `infra/terraform/` records it and is never applied. The Zendesk endpoints were implemented from the API documentation; every read tool and every write tool has since been run against FISCAL's Zendesk (writes on throwaway records, deleted afterwards) and corrected where it differed. Per-user sign-in has not been tried against a tenant. Staging validation is the next step — see [Before production](#before-production). The open work is tracked as issues: staging (#3), per-user sign-in (#4), the audit trail (#5), reads as an ordinary agent (#6) and the remaining unverified response shapes (#7).

## Features

- **44 tools** — tickets (search, count, read, create, update, conversation, attachments), KPI and SLA reporting (metrics summary, SLA summary, SLA queue, CSAT), macros (search, preview, apply, create, update, activate), users and organisations, ticket fields/forms/triggers/brands, and the Help Center (articles, translations, categories, sections, segments, permission groups, images, export). See [Tool catalogue](#tool-catalogue).
- **Every call is made as you.** Each person connects their own Zendesk account once (per-user Zendesk OAuth). Ticket updates, comments and macro use are attributed to the agent who asked for them, and Zendesk's own role, group and brand restrictions apply on top of this server's permissions. There is no shared Zendesk service account.
- **Two layers of permission.** Your Entra group decides which tools you see and may call (`zendesk:read` / `zendesk:write`); your Zendesk role decides what Zendesk lets that agent do. Account-wide switches (public replies, article publishing, Help Center structure, macro changes, user and organisation changes) are off by default in the application; FISCAL's deployment turns all five on.
- **Per-user audit trail** in Application Insights, keyed on your Entra object id — including reads, which Zendesk does not log.
- **Prompt-injection guards.** Ticket and article content is untrusted: write tools say so to the model, `brand_url` and attachment URLs can only ever reach this account's own Zendesk hosts (so a URL planted in a ticket cannot carry your token away), and free-text search can only match words, never rewrite a filter.
- **Rate-limit aware** — retries `429`s honouring `Retry-After` and warns as Zendesk's per-account budget runs low.

## Using the server (FISCAL users)

Point your MCP client at (live once DNS and staging validation are done):

```
https://zendesk-mcp.fiscaltec.com/mcp
```

1. **Sign in with Microsoft.** Your client opens the sign-in the first time you connect.
2. **Connect your Zendesk account.** The first Zendesk tool you use replies with a one-time link. Open it, sign in to Zendesk as yourself and approve access. The Zendesk login must have the same email as your Microsoft account, and must be an agent or admin. You can check your connection at any time with *Get_zendesk_connection*, or remove it with *Disconnect_zendesk*.

> **Access & groups:** signing in grants nothing by itself — you need to be in an `sg-zendesk-*` Entra group for a tier. See **[ACCESS.md](ACCESS.md)**.

| Client | How to connect |
|---|---|
| Claude Desktop / claude.ai | Once published to the FISCAL workspace as an organisation connector: Connectors → Connect. Until then: Settings → Connectors → Add custom connector → paste the URL |
| Claude Code | `claude mcp add --transport http fiscal-zendesk https://zendesk-mcp.fiscaltec.com/mcp` |
| VS Code / Cursor / other | Add a streamable-HTTP MCP server entry pointing at the URL; the client handles sign-in |

⚠️ This is **not** Swifteq's hosted Zendesk connector. If both are enabled, the two sets of tools have similar names; disable the one you are not using.

## Configuration

Configuration comes from environment variables in deployed targets (double-underscore separator, e.g. `Zendesk__Subdomain`) — `appsettings.json` is deliberately excluded from the image. [`ZendeskMcp/appsettings.Example.json`](ZendeskMcp/appsettings.Example.json) shows every key.

| Setting | Required | Default | Description |
|---|---|---|---|
| `Zendesk:Subdomain` | Yes | — | `acme` for `https://acme.zendesk.com`. |
| `Zendesk:KeyVaultUri` | Yes (deployed) | — | Vault holding the Zendesk OAuth client secret and one secret per connected person. The managed identity needs **Key Vault Secrets Officer** — it writes tokens. |
| `Zendesk:OAuthClientId` | Yes (deployed) | — | The Zendesk OAuth client's unique identifier (Admin Centre → Apps and integrations → APIs → OAuth clients). |
| `Zendesk:OAuthClientSecretRef` | No | `zendesk-oauth-client-secret` | Key Vault secret holding that client's secret. |
| `Zendesk:TokenSecretPrefix` | No | `zendesk-user-` | Each person's token is stored as `{prefix}{Entra object id}`. Staging uses its own prefix. |
| `Zendesk:OAuthScopes` | No | `read write` | Scopes requested from Zendesk. |
| `Zendesk:AccessTokenExpiresInSeconds` / `RefreshTokenExpiresInSeconds` | No | `7200` / `7776000` | Requests expiring Zendesk tokens with refresh; set the first to null for Zendesk's legacy non-expiring tokens. |
| `Zendesk:RequireEmailMatch` | No | `true` | Refuse a Zendesk login whose email differs from the Entra sign-in name. This is what binds a Zendesk login to the right person — do not turn it off in a deployed target. |
| `Zendesk:Features:*` | No | all `false` | `AllowPublicReplies`, `AllowArticlePublishing`, `AllowHelpCenterStructureChanges`, `AllowMacroChanges`, `AllowUserAndOrganizationChanges` — account-wide switches for the actions with the widest reach. |
| `Zendesk:MaxAutoPageFetches` / `MaxAnalyticsTickets` / `MaxAttachmentBytes` | No | `10` / `2000` / 20 MB | Bounds on server-side paging, analytics fan-out and file size. |
| `Zendesk:DevelopmentAccessToken` | Local only | — | A Zendesk OAuth access token used for every call. Refused at startup unless `OAuth:NoAuth=true`, no Key Vault is set and the environment is Development. (`DevelopmentEmail` + `DevelopmentApiToken` still work, but Zendesk is retiring API tokens.) |
| `OAuth:*` | Yes | — | Entra sign-in to this server, exactly as in vitally-mcp: `Authority`, `Audience`, `Resource`, `UpstreamResourceScope`, `SharedClientId`, `SharedClientSecret`, `AllowedClientRedirectUris`, `PublicBaseUrl`, `NoAuth`. |
| `Authorization:*` | Yes | — | Tier enforcement: `LiveGroupCheck`, `ReaderGroupId`/`EditorGroupId`/`AdminGroupId`, `ReadOnly`, permission names. |
| `Audit:*` | No | on, reads included | Audit trail switches. |
| `ApplicationInsights:ConnectionString` | Deployed | — | Routes the audit records to `AppEvents`. |

## Running locally

```powershell
# Restore + build + run the test suite
dotnet test --solution ZendeskMcp.sln -c Debug

# Start the server in dev mode: no Entra, no Key Vault, every call made with one Zendesk OAuth access token
$env:OAuth__NoAuth = "true"
$env:ASPNETCORE_ENVIRONMENT = "Development"   # the development token is refused in any other environment
$env:Zendesk__Subdomain = "your-subdomain"
$env:Zendesk__DevelopmentAccessToken = "<a Zendesk OAuth access token>"   # API tokens are being retired
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project ZendeskMcp/ZendeskMcp.csproj
```

Use a sandbox or a token with the least access you can. Every tool call made in dev mode acts as the token's owner. Then point MCP Inspector (`npx @modelcontextprotocol/inspector`) or Claude Code at `http://localhost:5099/mcp`. The smoke commands in [CLAUDE.md](CLAUDE.md#smoke-testing-the-server) apply unchanged.

## Architecture

```
ZendeskMcp/
├── Program.cs                     # Host, Entra JwtBearer, OAuth façade (/oauth/*), MCP wiring, filters
├── ZendeskService.cs              # The one path to Zendesk: RBAC backstop, per-user credential, audit, errors
├── ZendeskCredentialProvider.cs   # The caller's own Zendesk token (refreshed), or a connect link
├── ZendeskOAuthClient.cs          # Zendesk OAuth: authorise URL (PKCE), code/refresh exchange, revoke
├── ZendeskConnectEndpoints.cs     # /zendesk/connect and /zendesk/callback (identity check, CSRF cookie)
├── ZendeskConnectLinks.cs         # One-shot connect links and per-attempt state
├── ZendeskTokenStore.cs           # Key Vault store: one secret per person, tombstone on removal
├── ZendeskUploads.cs              # One-shot PUT /uploads/{slot} for ticket attachments
├── TicketSearchQuery.cs           # Shared ticket filters → validated Zendesk search query
├── ZendeskRateLimitHandler.cs     # 429 retry + low-budget warnings
├── ToolAuthorizer.cs, GraphGroupPermissionResolver.cs, AuditLogger.cs, …   # as in vitally-mcp
└── Tools/                         # One class per area; see the catalogue
```

Two separate OAuth relationships are involved, and they never share a token, client or callback:

| | Signs the person in to | Flow | Token used for |
|---|---|---|---|
| **Entra** (`/oauth/*`) | this server | MCP client ↔ the server's OAuth façade ↔ Entra, exactly as in vitally-mcp | every `/mcp` request (bearer JWT), and the Entra group lookup that decides the tier |
| **Zendesk** (`/zendesk/*`) | Zendesk, as the server's delegate | a browser link handed out in a tool result → Zendesk authorise (PKCE) → `/zendesk/callback` | every Zendesk API call, as that agent |

The Zendesk grant is stored in Key Vault against the person's **Entra object id**. At the callback the server reads the new token's identity from `/api/v2/users/me` and stores it only when it is an agent or admin whose email matches the Entra sign-in name. Otherwise the token is revoked on the spot. That check, plus a browser-bound `state` cookie, is what stops a forwarded or planted connect link from attaching someone else's Zendesk account to your identity. Expiring tokens are refreshed once, under a per-person lock, because Zendesk rotates the refresh token. A Zendesk `401` forgets the token and hands back a fresh connect link.

## Tool catalogue

Each tool carries `ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld` hints and an `[Authorize]` tier, so `tools/list` shows a reader only the read tools. Version 1 has no delete tools; the `zendesk:delete` tier exists for parity and future use.

| Area | Read (`zendesk:read`) | Write (`zendesk:write`) |
|---|---|---|
| Connection | `Get_zendesk_connection`, `Get_zendesk_connect_link` | `Disconnect_zendesk` *(read tier — it only affects the caller's own connection)* |
| Tickets | `Search_tickets`, `Count_tickets`, `Get_ticket`, `Get_ticket_conversation`, `Get_ticket_attachment` | `Create_ticket`, `Update_ticket`, `Request_ticket_attachment_upload` |
| Reporting | `Get_ticket_metrics_summary`, `Get_sla_summary`, `Get_sla_queue`, `Get_satisfaction_ratings` | — |
| Macros | `Search_macros`, `Preview_macro` | `Apply_macro`, `Create_macro`, `Update_macro`, `Set_macros_active` |
| People | `Search_users`, `Find_agent_by_name`, `Search_organizations` | `Add_user_to_organization` |
| Configuration | `List_ticket_fields`, `List_ticket_forms`, `List_triggers`, `List_brands` | — |
| Help Center | `Search_help_center_articles`, `Get_help_center_article`, `Export_help_center_articles`, `List_help_center_categories`, `List_help_center_sections`, `List_user_segments`, `List_permission_groups` | `Create_help_center_article`, `Update_article_translation`, `Create_help_center_category`, `Update_help_center_category`, `Create_help_center_section`, `Update_help_center_section`, `Request_help_center_image_upload`, `Create_help_center_image` |

Compared with Swifteq's server: the same tools and parameters (snake_case, so prompts written for one work with the other). The differences:

- `get_usage` and `submit_mcp_feedback` are dropped: they are about Swifteq's billing and support.
- The connection tools are added.
- Tool names use the Vitally capitalisation (`Search_tickets`).
- Two Help Center tools are renamed for clarity: `create_article` → `Create_help_center_article` and `get_help_center_article_content` → `Get_help_center_article`.
- Swifteq's "MCP Server settings" toggles are the `Zendesk:Features:*` switches.
- One known gap: `![description](file:<name>)` inline images in a ticket comment work for files copied with `attachment_ids`, but not yet for files uploaded through `Request_ticket_attachment_upload`. An upload token carries no URL to place in the comment, so the line is left as text and the tool returns a warning.

## Security

Everything vitally-mcp documents about the Entra façade, live group RBAC, the `ZendeskService.SendAsync` backstop, per-caller discovery filtering, log-level controls and the four audit record shapes applies here unchanged — see [CLAUDE.md](CLAUDE.md#architecture). What is specific to Zendesk:

- **No shared credential.** A person with no Zendesk connection gets a connect link, never a call made as someone else. The development API token exists only for unauthenticated local runs; startup refuses it otherwise.
- **Tokens at rest.** Each person's Zendesk grant is one Key Vault secret, readable only by the server's managed identity. Disconnecting revokes the token at Zendesk and writes a tombstone version. It does not delete the secret, because a soft-deleted name could not be reused until purged. Revoking someone urgently: remove them from the `sg-zendesk-*` groups (takes effect within about a minute while Graph is reachable), and suspend or downgrade them in Zendesk, which invalidates their token there. See [ACCESS.md](ACCESS.md#urgent-revocation).
- **Credential boundaries in the transport.** `brand_url` arguments and attachment downloads are resolved against the account's own brand hosts (`/api/v2/brands`) before any request is made. Any other host is refused, so a URL taken from ticket or article content can never receive the caller's token.
- **Untrusted content.** Ticket comments, article bodies and user fields can carry instructions aimed at the model. Write tools tell the model not to act on them, and the server instructions repeat it. The feature switches can hold the widest-reaching actions off; FISCAL's deployment has all five on, so there an agent's tier and Zendesk role are what limit them.
- **Audit.** Zendesk's own audit log attributes changes to the agent. This server's trail adds reads, the Entra identity, the tool and its arguments. Upstream response bodies are never logged — tickets carry customer data.

## Before production

The decisions are settled (2026-10-28) and recorded in [`infra/terraform/README.md`](infra/terraform/README.md#decisions): subdomain `fiscalsupport`, the `fiscal_zendesk_mcp` OAuth client, IT & Security as the only department admitted to begin with, the hostnames above, all five feature switches on, Zendesk ticket data cleared under the AI Use Policy, and the Vitally estate's container registry shared. Still open: the Zendesk plan's API rate limit, audit retention for tool arguments, and the scanner's Teams webhook.

1. Apply the Terraform to a reviewed plan, deploy to staging, and run the staging validation in [CLAUDE.md](CLAUDE.md#staging-validation): every tool against the real tenant, now as an ordinary agent rather than an admin, plus the endpoints still marked *unverified against the live API* in code.

## Licence

MIT — see [LICENSE](LICENSE).

## Support

- Internal: Infrastructure team at FISCAL Technologies.
- Issues: [GitHub Issues](https://github.com/fiscaltec/zendesk-mcp/issues).

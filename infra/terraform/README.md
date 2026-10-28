# Zendesk MCP — Infrastructure as Code (Terraform)

The whole Azure and Entra estate for the Zendesk MCP server, in **IT-Production /
`zendesk-prod-rg-uksouth`** (UK South), described as Terraform. It is a **record, never applied**:
the estate is built and changed with `az` CLI (`docs/runbooks/build-the-estate.md`), and these files
are kept in step by hand — the Vitally estate's practice. Nothing has been built yet.

The design is the Vitally MCP estate's (fiscaltec/vitally-mcp), rebuilt for this server. Comments
cite `vitally-mcp#NNN` where a lesson was learned there.

> **Validated, not planned.** `terraform fmt -check -recursive` and `terraform validate` pass
> (after `terraform init -backend=false`, which resolved azurerm 4.81.0 and azuread 3.10.0 on
> 2026-10-28). Those versions are pinned in the committed `.terraform.lock.hcl`, with checksums for
> linux_amd64, linux_arm64, windows_amd64, darwin_amd64 and darwin_arm64. Move them deliberately with
> `terraform init -upgrade`, then re-run `terraform providers lock` with the same `-platform` list. `validate` checks syntax and provider schemas only: it does not reach Azure, so values
> the API rejects (container `cpu`/`memory` pairs, SKUs, region availability) will first show at
> `terraform plan` — which, for a record, never runs; the `az` build is where they surface instead.
> Re-validated on 2026-10-28 after the decisions below (shared registry, CMK vault removed).

## Layout
| File | Contents |
|---|---|
| `providers.tf` | azurerm (~> 4.0) and azuread (~> 3.0) providers; no backend (a record, never applied) |
| `variables.tf` | inputs — undecided values have **no default**, secrets are `sensitive` |
| `network.tf` | the resource group, VNet, subnets (`snet-app`, `snet-pe`), NAT gateway + PIP, private DNS zones |
| `identity.tf` | user-assigned managed identity, its role assignments, the GitHub federated credentials |
| `keyvault.tf` | secret vault (private) + private endpoint + diagnostics |
| `acr.tf` | the Vitally estate's ACR, **shared** (a data source) + this VNet's private endpoint onto it |
| `monitoring.tf` | Log Analytics + Application Insights (private ingestion, local auth off) |
| `ampls.tf` | Azure Monitor Private Link Scope, its subnet, DNS zones and private endpoint |
| `diagnostics.tf` | Container Apps environment → Log Analytics (system and console logs) |
| `flowlogs.tf` | VNet flow logs → hardened storage account |
| `containerapps.tf` | Container Apps environment, the production app, the secret-expiry scanner Job (`scan/run.py`) |
| `containerapps-staging.tf` | the staging app — a second app in the same environment |
| `entra.tf` | Entra app registration + service principal, Gate 1 assignments, `sg-zendesk-*` tier groups |
| `outputs.tf` | the values the out-of-band steps below need |

## What gets built
- **Two Container Apps** in one VNet-injected environment: `zendesk-prod-ca-uksouth` (one warm
  replica) and `zendesk-staging-ca-uksouth` (scales to zero). Both are pinned to **at most one
  replica**: connect links, OAuth state and upload slots live in an in-process cache
  (`containerapps.tf` explains; scaling out needs Redis first).
- **One secret vault**, `zendesk-prod-kv-uksouth`, private-endpoint only. It holds the Zendesk OAuth
  client secret (`zendesk-oauth-client-secret`), the record copy of the Entra client secret
  (`entra-mcp-client-secret`), and one secret per connected person, which the app writes itself —
  `zendesk-user-{objectId}` for production, `zendesk-staging-user-{objectId}` for staging, so the two
  targets never share a person's token. The app's identity therefore holds **Key Vault Secrets
  Officer**; `identity.tf` sets out why one vault with that grant was preferred to two.
- **One Entra app**, `Zendesk MCP`, serving both targets, plus three tier groups
  (`sg-zendesk-readers` / `-editors` / `-admins`) whose ids feed `Authorization__*GroupId`.
- **No registry of its own.** Images go into the Vitally estate's private ACR, `vitallyproducruksouth`
  (repository `zendesk-mcp`), through a second private endpoint from this VNet. `acr.tf` sets out the
  cost: the deploy identity's import right (a custom role, `identity.tf`) can overwrite Vitally's tags.
- Its **own** Log Analytics + Application Insights behind AMPLS, NAT egress, flow logs,
  and a weekly Key Vault expiry scanner posting to Teams. The scanner skips per-person token secrets
  (tag `kind=zendesk-user-token`): their expiry is the refresh token's and heals itself — the person
  is offered a connect link and reconnects.

## Decisions
Settled on 2026-10-28 and committed as defaults, so a plan needs no further input on them:

| Decision | Value | Where |
|---|---|---|
| Zendesk subdomain | `fiscalsupport` | `zendesk_subdomain` |
| Zendesk OAuth client | `fiscal_zendesk_mcp` (confidential, PKCE) | `zendesk_oauth_client_id` |
| Gate 1 — who may sign in | IT & Security Department only, to begin with | `entra_gate1_group_object_ids` (`entra.tf`) |
| Gate 2 — tier membership | IT & Security Department nested in `sg-zendesk-readers`; Dan Searle in all three tier groups | out-of-band, build runbook Phase 12 |
| Hostnames | `https://zendesk-mcp.fiscaltec.com`, `https://zendesk-mcp-staging.fiscaltec.com` | `public_base_url` and friends |
| Feature switches | all five **on**, on both targets (staging writes stay denied while `staging_read_only` is true) | `zendesk_allow_*`, `staging_zendesk_allow_*` |
| AI Use Policy | Zendesk ticket data cleared for processing with Claude | — |
| Monitoring | its own Log Analytics workspace and Application Insights component | `monitoring.tf` |
| Container registry | **shared** with the Vitally estate (`vitallyproducruksouth`), accepting the cross-estate write access `acr.tf` describes | `acr_name`, `acr_resource_group_name` |
| Build method | `az` CLI (`docs/runbooks/build-the-estate.md`); this Terraform is a record, never applied — as Vitally's | — |
| VNet range | `10.81.0.0/23`, confirmed free | `vnet_address_space` |

Still open:

1. **Zendesk plan rate limit** — the account's API limit per minute depends on the Zendesk plan and
   add-ons, and is shared by everyone connected (and by every other integration on the account).
   Confirm it before the analytics tools (which page through up to 2,000 tickets a call) are opened
   up widely.
2. **Audit retention** — how long `AppEvents` keeps tool arguments, which can name customers
   (`monitoring.tf` sets the workspace retention).
3. **The Teams webhook** for the expiry scanner (`TF_VAR_teams_webhook_url`).

## Building and changing the estate

**Not with Terraform.** As for the Vitally estate (fiscaltec/vitally-mcp `infra/terraform/README.md`,
"Adoption"), this configuration is a record that is **never applied**: there is no backend and no
state, so a plan would read every resource as a create and an apply would collide with what exists.

- **Build:** [`docs/runbooks/build-the-estate.md`](../../docs/runbooks/build-the-estate.md), an `az`
  CLI procedure for Azure Cloud Shell that builds exactly what these files describe. Its coverage table
  maps each resource here to the step that builds it, and its prerequisites list the elevations needed.
- **Change:** make the change with `az`, then edit these files to match **in the same PR**, and run
  `terraform init -backend=false && terraform validate`. A record that drifts from the estate is worse
  than none.
- **Adopt** (make Terraform the source of truth) only as a deliberate project: a remote backend outside
  `zendesk-prod-rg-uksouth`, an import block per resource, and a plan read line by line.

> ⚠️ Secret values — the Zendesk OAuth client secret, the Entra client secret and every per-person
> token — live only in Key Vault (and the Entra secret's copy on the two Container Apps). Never put
> one in a file in this repository.

## CI/CD
`.github/workflows/deploy.yml` deploys to one of two targets, named by GitHub **environment**:
`production` (the default) or `staging`; the nightly release train ships to both, staging first.
Each target needs a federated credential on the managed identity whose subject is
`repo:fiscaltec/zendesk-mcp:environment:<target>` (`identity.tf`), `Contributor` on that target's
Container App (`identity.tf`), and `CONTAINER_APP` + `PUBLIC_ORIGIN` as environment-scoped GitHub
variables. The identity also holds `ContainerApp Reader` on the resource group, so the train can tell
a missing staging app from a failed lookup. Deploys build to GHCR and `az acr import` by digest into
the shared private ACR.

## Known limitations
- **Managed certificates and DNS** are out-of-band (build runbook, Phase 11).
- **Secret values** — the Zendesk OAuth client secret, the Entra client secret's vault copy and every
  per-person token live only in Key Vault; Terraform manages none of them.
- **The registry belongs to the Vitally estate.** Its CMK, retention and diagnostics are managed in
  fiscaltec/vitally-mcp; a change there (a rename, a network rule, a purge task) reaches this estate.
- **Graph app permission and admin consent** are out-of-band (build runbook, Phase 10), deliberately: both need a
  more privileged principal than routine applies should run as.

# Zendesk MCP — Infrastructure as Code (Terraform)

The whole Azure and Entra estate for the Zendesk MCP server, in **IT-Production /
`zendesk-prod-rg-uksouth`** (UK South). It is **greenfield**: nothing here exists yet, and this
configuration creates all of it — the resource group included — when it is applied, deliberately,
after review. From then on it is the source of truth; a change made by hand with `az` is drift the
next apply reverts.

The design is the Vitally MCP estate's (fiscaltec/vitally-mcp), rebuilt for this server. Comments
cite `vitally-mcp#NNN` where a lesson was learned there.

> **Validated, not planned.** `terraform fmt -check -recursive` and `terraform validate` pass
> (after `terraform init -backend=false`, which resolved azurerm 4.81.0 and azuread 3.10.0 on
> 2026-10-28). Those versions are pinned in the committed `.terraform.lock.hcl`, with checksums for
> linux_amd64, linux_arm64, windows_amd64, darwin_amd64 and darwin_arm64. Move them deliberately with
> `terraform init -upgrade`, then re-run `terraform providers lock` with the same `-platform` list. `validate` checks syntax and provider schemas only: it does not reach Azure, so values
> the API rejects (container `cpu`/`memory` pairs, SKUs, region availability) will first show at
> `terraform plan`. Nothing has been planned or applied. Re-validated on 2026-10-28 after the
> decisions below (shared registry, CMK vault removed), with the same pinned providers.

## Layout
| File | Contents |
|---|---|
| `providers.tf` | azurerm (~> 4.0) and azuread (~> 3.0) providers; **local** state (no backend block) |
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
  cost: the deploy identity's Contributor on that registry can overwrite Vitally's images.
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
| Gate 2 — tier membership | IT & Security Department nested in `sg-zendesk-readers`; Dan Searle in all three tier groups | out-of-band, step 8 |
| Hostnames | `https://zendesk-mcp.fiscaltec.com`, `https://zendesk-mcp-staging.fiscaltec.com` | `public_base_url` and friends |
| Feature switches | all five **on**, on both targets (staging writes stay denied while `staging_read_only` is true) | `zendesk_allow_*`, `staging_zendesk_allow_*` |
| AI Use Policy | Zendesk ticket data cleared for processing with Claude | — |
| Monitoring | its own Log Analytics workspace and Application Insights component | `monitoring.tf` |
| Container registry | **shared** with the Vitally estate (`vitallyproducruksouth`), accepting the cross-estate write access `acr.tf` describes | `acr_name`, `acr_resource_group_name` |
| State | local, as the Vitally estate keeps it (`providers.tf` sets the handling rules) | — |
| VNet range | `10.81.0.0/23`, confirmed free | `vnet_address_space` |

Still open:

1. **Zendesk plan rate limit** — the account's API limit per minute depends on the Zendesk plan and
   add-ons, and is shared by everyone connected (and by every other integration on the account).
   Confirm it before the analytics tools (which page through up to 2,000 tickets a call) are opened
   up widely.
2. **Audit retention** — how long `AppEvents` keeps tool arguments, which can name customers
   (`monitoring.tf` sets the workspace retention).
3. **The Teams webhook** for the expiry scanner (`TF_VAR_teams_webhook_url`).

## Prerequisites
- Terraform >= 1.6, `az login` against subscription `282207c6-…` (IT-Production), tenant `75bd6050-…`.
- The applying principal needs, **in Azure**: Owner on the subscription for the first apply (it
  creates the resource group and role assignments; Contributor + Role Based Access Control
  Administrator also works), and Network Contributor on `NetworkWatcherRG` for the flow log. The
  subscription-level role also covers the **shared registry** in `vitally-prod-rg-uksouth`: this
  configuration assigns roles on it and approves a private endpoint onto it. **In
  Entra**: Application Administrator, Groups Administrator, and the right to assign the app's
  default role to groups. Through PIM, for the duration of the apply.
- A Teams Workflows webhook for the scanner (`TF_VAR_teams_webhook_url`).

> ⚠️ Terraform persists the Entra client secret and the Teams webhook URL in **state**, even though
> the variables are `sensitive`. State is local (`infra/terraform/terraform.tfstate`, gitignored): apply
> only from a FISCAL-managed machine with an encrypted disk, never commit or share the file, and hand
> the current copy to whoever applies next. Never commit a filled-in `terraform.tfvars` either.

## Apply procedure
The order matters because two things cannot exist until something else does: the **Entra client
secret** needs the app registration, and the **first image** needs the private endpoint onto the
shared ACR; and both Container Apps need both (each refuses to plan without the secret, and cannot start without an image).

**0. Before Terraform — the Zendesk OAuth client.** It exists: `fiscal_zendesk_mcp`, created on
2026-10-28 and used for the live validation. In Zendesk Admin Centre → Apps and integrations → APIs →
OAuth clients, edit it:
- **Client kind: Confidential** (it already is). Remove `http://localhost:5099/zendesk/callback`.
- **Redirect URLs — both targets:**
  `https://zendesk-mcp.fiscaltec.com/zendesk/callback` and
  `https://zendesk-mcp-staging.fiscaltec.com/zendesk/callback`. One client serves both; a missing
  URL fails that target's connect flow at Zendesk.
- Its **unique identifier** is `zendesk_oauth_client_id`.
- **Regenerate the secret** (the current one was handled by hand during validation). It is shown once.
  Keep it for step 2 — it goes into Key Vault, never into Terraform.

**1. Init, plan, read the plan, then the foundation.**
```bash
cd infra/terraform
terraform init
export TF_VAR_teams_webhook_url='…'
# Plan to a file, read it line by line, and apply exactly that file. Everything should read as a
# create, with the names, hostnames and groups you decided. (An untargeted plan stops at the
# Container Apps' secret precondition until step 2 — expected.)
terraform plan -out=foundation.tfplan \
  -target=azuread_application_pre_authorized.self \
  -target=azuread_application_api_access.self \
  -target=azuread_application_api_access.msgraph \
  -target=azuread_app_role_assignment.gate1 \
  -target=azuread_group.zendesk_readers \
  -target=azuread_group.zendesk_editors \
  -target=azuread_group.zendesk_admins \
  -target=azurerm_private_endpoint.acr \
  -target=azurerm_private_endpoint.kv \
  -target=azurerm_role_assignment.mi_acr_pull \
  -target=azurerm_role_assignment.mi_acr_contributor \
  -target=azurerm_role_assignment.mi_kv_secrets_officer \
  -target=azurerm_federated_identity_credential.github_actions_prod \
  -target=azurerm_federated_identity_credential.github_actions_staging
terraform apply foundation.tfplan
```
This creates the resource group, network, vault, the private endpoint onto the shared ACR, the
identity and its roles, and the Entra objects.

**2. Secrets (out-of-band).**
- Create the Entra client secret, 180-day expiry:
  `az ad app credential reset --id "$(terraform output -raw entra_app_object_id)" --append --display-name "zendesk-mcp $(date +%F)" --end-date <yyyy-mm-dd>`.
  The rotation procedure is `docs/runbooks/entra-and-zendesk-oauth.md`.
- Store both secrets in the vault, each with an expiry so the scanner warns before rotation is due:
  ```bash
  az keyvault secret set --vault-name zendesk-prod-kv-uksouth --name entra-mcp-client-secret \
    --value '…' --expires <yyyy-mm-ddT00:00:00Z>
  az keyvault secret set --vault-name zendesk-prod-kv-uksouth --name zendesk-oauth-client-secret \
    --value '…' --expires <yyyy-mm-ddT00:00:00Z>
  ```
  The vault's data plane is reachable only through its private endpoint. Run these from inside the
  VNet, or open it briefly to your own address
  (`az keyvault update -n zendesk-prod-kv-uksouth --public-network-access Enabled --default-action Deny`,
  `az keyvault network-rule add -n zendesk-prod-kv-uksouth --ip-address <ip>/32`) and close it again
  straight afterwards (`--public-network-access Disabled`, `network-rule remove`). You also need a
  data-plane role on the vault (Key Vault Secrets Officer), time-boxed; remove it when done.
- `export TF_VAR_oauth_shared_client_secret='…'` (the same Entra secret). It is copied onto both
  apps as `entra-oauth-client-secret`; the vault copy is the record, not a reference.

**3. The first image.** `deploy.yml` rolls an *existing* app, so the first image is imported by hand.
Build from `main` and push to GHCR, then import server-side into the shared ACR (public access is
off). Name the `zendesk-mcp` repository explicitly: the registry also holds `vitally-mcp`.
```bash
tag="sha-$(git rev-parse --short HEAD)"
docker build -t ghcr.io/fiscaltec/zendesk-mcp:$tag . && docker push ghcr.io/fiscaltec/zendesk-mcp:$tag
az acr import --name vitallyproducruksouth --source ghcr.io/fiscaltec/zendesk-mcp:$tag \
  --image zendesk-mcp:$tag --username <github user> --password <PAT with read:packages>
```
Set `image_tag` and `staging_image_tag` to `$tag`. If the GHCR package was created by this push,
grant the repository write access to it (package settings → Manage Actions access) so `deploy.yml`
can push later.

**4. The full apply.** `terraform plan`, read it — the Container Apps, scanner Job, monitoring,
AMPLS, flow logs and role assignments should be the only creates — then `terraform apply`. From now
on Terraform ignores the apps' image; `deploy.yml` owns it.

**5. Graph permission for the managed identity.** Gate 2 reads `sg-zendesk-*` membership with the
identity's own Graph **application** permission `GroupMember.Read.All`. Without it every tool call is
denied. Needs a Privileged Role Administrator:
```bash
mi=$(terraform output -raw managed_identity_principal_id)
graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
role=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 \
  --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
  --body "{\"principalId\":\"$mi\",\"resourceId\":\"$graph\",\"appRoleId\":\"$role\"}"
```

**6. Tenant-wide admin consent** for `openid profile email offline_access mcp.access`, so nobody sees
a consent screen: `az ad app permission admin-consent --id "$(terraform output -raw entra_app_client_id)"`.

**7. Custom domains with managed certificates.** `fiscaltec.com` is on Cloudflare, so DNS is not
managed here. For each host, an **un-proxied** (DNS-only) `CNAME` to the app's default FQDN
(`production_default_fqdn` / `staging_default_fqdn`) and an `asuid.<host>` `TXT` carrying
`custom_domain_verification_id`. Proxying the CNAME breaks managed-certificate issuance. Then:
```bash
az containerapp hostname add  -n zendesk-prod-ca-uksouth -g zendesk-prod-rg-uksouth --hostname zendesk-mcp.fiscaltec.com
az containerapp hostname bind -n zendesk-prod-ca-uksouth -g zendesk-prod-rg-uksouth --hostname zendesk-mcp.fiscaltec.com \
  --environment zendesk-prod-cae-uksouth --validation-method CNAME
```
and the same for `zendesk-staging-ca-uksouth` / `zendesk-mcp-staging.fiscaltec.com`. azurerm cannot
create managed certificates, which is why this is out-of-band. If a later plan proposes removing a
binding, add it to that app's `ignore_changes` rather than applying.

**8. Tier membership.** Nest each admitted department group into one `sg-zendesk-*` group (Gate 2)
— see `ACCESS.md`. As decided, the IT & Security Department reads, and Dan Searle holds every tier
(tiers are cumulative, so `sg-zendesk-admins` alone would do; all three keeps the record explicit):
```bash
az ad group member add --group sg-zendesk-readers --member-id 6ba9bf61-e959-4dd9-8a96-d477a35b0d03
me=$(az ad user show --id dsearle@fiscaltec.com --query id -o tsv)
for g in readers editors admins; do az ad group member add --group "sg-zendesk-$g" --member-id "$me"; done
``` Gate 1 (`entra_gate1_group_object_ids`) must name the department groups
**directly**: the app-assignment gate ignores nesting, so assigning a tier group would admit only its
direct members.

**9. GitHub.** Repo variables `ACR_NAME=vitallyproducruksouth`,
`RESOURCE_GROUP=zendesk-prod-rg-uksouth`, `IMAGE_NAME=zendesk-mcp`; repo secrets `AZURE_CLIENT_ID`
(`managed_identity_client_id`), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. Environments
`production` and `staging`, each with `CONTAINER_APP` (`zendesk-prod-ca-uksouth` /
`zendesk-staging-ca-uksouth`) and `PUBLIC_ORIGIN` (the origin, no path or trailing slash).

**10. Verify.** For each origin: `/health` 200, unauthenticated `POST /mcp` 401,
`bash .github/scripts/verify-oauth-metadata.sh <origin>`; then sign in from an MCP client, call
`Get_zendesk_connection`, follow the connect link, and confirm a tool call runs as you. A wrong
Entra secret passes every check except signing in (`invalid_client` at `/oauth/token`). For
staging, confirm what is actually serving:
`bash .github/scripts/check-serving-revisions.sh zendesk-staging-ca-uksouth zendesk-prod-rg-uksouth --env Authorization__ReadOnly --equals true --env ApplicationInsights__ConnectionString --env Zendesk__TokenSecretPrefix --equals zendesk-staging-user-`.

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
- **Managed certificates and DNS** are out-of-band (step 7).
- **Secret values** — the Zendesk OAuth client secret, the Entra client secret's vault copy and every
  per-person token live only in Key Vault; Terraform manages none of them.
- **The registry belongs to the Vitally estate.** Its CMK, retention and diagnostics are managed in
  fiscaltec/vitally-mcp; a change there (a rename, a network rule, a purge task) reaches this estate.
- **Graph app permission and admin consent** are out-of-band (steps 5–6), deliberately: both need a
  more privileged principal than routine applies should run as.

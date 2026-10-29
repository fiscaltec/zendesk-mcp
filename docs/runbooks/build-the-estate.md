# Build the estate with `az` CLI

This runbook builds the whole Azure and Entra estate for the Zendesk MCP server: the resource group,
network, monitoring, vault, identity, Entra app and groups, both Container Apps and the scanner Job,
then the out-of-band steps that were always manual (secrets, the first image, the Graph grant, admin
consent, custom domains, tier membership, GitHub).

> ⚠️ **Written 2026-10-28 from `infra/terraform/*.tf` and NOT yet executed against Azure.** No command
> here has been run: the authoring session had no Azure access. Flags were checked against the
> `az` 2.91 help text and source where that was possible offline; the ones that could not be are
> listed under [Unverified `az` behaviour](#unverified-az-behaviour). **Stop at the first `FAIL`, the
> first error, or the first output you did not expect**, fix the cause (here *and* in this file),
> and re-run that block. Do not push on past a failure to "see what happens".

**Why `az` and not Terraform.** The team's standing practice, as for the Vitally estate
(fiscaltec/vitally-mcp `infra/terraform/README.md`, "Adoption"), is that the Terraform is a record
that is never applied and the live estate is built and changed with `az`. This runbook therefore
builds *exactly* what `infra/terraform/` describes — the same names, SKUs, settings, CIDRs, roles,
environment variables, secrets and scale — so the Terraform stays an accurate record. **If you have
to change a value to make a step work, change the Terraform to match in the same PR.** The
[coverage table](#coverage-terraform-resource--step) maps every Terraform resource to the step that
builds it.

**What already exists, and is not created here:** the shared registry `vitallyproducruksouth` in
`vitally-prod-rg-uksouth` (Vitally's — this estate adds a private endpoint, a custom import role and
AcrPull; `acr.tf` sets out the cross-estate cost), the Entra group *IT & Security Department*, the
Zendesk OAuth client `fiscal_zendesk_mcp`, and the subscription's `NetworkWatcher_uksouth`.

## How to run it

- **Shell: Azure Cloud Shell (Bash).** The owner works on Windows; Cloud Shell gives a Linux `bash`
  with `az`, `gh`, `git`, `curl`, `python3` and `dig` already installed, so nothing needs installing
  locally and no Terraform is needed. Open it from the portal (`>_`) and choose **Bash**, not
  PowerShell. The one exception is building the first image (Phase 8), which needs Docker — Cloud
  Shell has none, so that one block runs on a workstation.
- **Every block is a `( … )` subshell with `set -euo pipefail`.** A failing command ends the block
  without closing Cloud Shell. Paste one block at a time and read its output before the next.
  ⚠️ **Only at an interactive prompt or through `bash <file>`.** Bash ignores `set -e` inside any
  command that is itself part of an `&&`/`||` list, so a tool that wraps what it runs (an agent
  harness, `eval "…" && …`) silently turns every block into one that carries on past a failure.
  Found running this from a Claude Code cloud session (2026-10-28, #3); there each block is saved to
  a file and run with `bash`.
- **From WSL, `az` must be the Linux build** (`command -v az` → `/usr/bin/az`). By default WSL also
  searches the Windows PATH, so `az` can be the Windows `az.cmd` (`/mnt/c/…/wbin/az`). Its `-o tsv`
  output ends in `\r`, which makes every comparison fail with identical-looking values
  (`]AIL subscription …`, where the `\r` returns the cursor and `]` overwrites the `F`). Install the
  Linux build with `curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash`; it keeps its own login
  and extensions. Found running Phases 7–9 from WSL (2026-10-28, #3).
- **Every block starts by sourcing `~/zendesk-estate.env`** (written by *Set these first*) and
  checking the subscription and tenant, so a block can be run after a Cloud Shell timeout (20 idle
  minutes) without re-pasting anything earlier. `~` is persisted by Cloud Shell; that file holds no
  secrets.
- **Secrets never go in `~`.** They are captured into `/tmp/zendesk-estate-secrets` (mode 700, not
  persisted by Cloud Shell) with `capture_secret`, which reads without echoing. Run each
  `capture_secret` line **on its own**, never inside a pasted block: a `read` inside a pasted block
  swallows the next pasted line as the secret.
- **Verify blocks fail loudly.** Each prints `OK …` per check, or `FAIL …` and stops. An empty
  answer from `az` is a `FAIL`, never a pass.
- **Re-runnable where `az` allows.** Creates are guarded with `show … || create …`. Where a step
  cannot be safely re-run, it says so.
- The Cloud Shell egress address (needed for the Key Vault window in Phase 7) is
  `curl -4 -s https://api.ipify.org`. The window resolves it itself, every time.

## Set these first

Run it once. Cloud Shell keeps `~` across sessions, so after a timeout you do not need it again.
Re-running it is safe: it rewrites the file but keeps image tags Phase 8 has already recorded. Every
value is a Terraform default from `variables.tf`, `entra.tf`, `keyvault.tf` and friends, or derived
from one exactly as the Terraform derives it. Fixed names that appear once (connection and DNS-group
names such as `kv-conn`) stay inline at the step that uses them.

```bash
_keep_tags=$(grep -E '^(STAGING_)?IMAGE_TAG=[A-Za-z0-9._-]+' ~/zendesk-estate.env 2>/dev/null || true)
cat > ~/zendesk-estate.env <<'ENVFILE'
# Zendesk MCP estate — names and settings from infra/terraform/*.tf defaults. Non-secret. Sourced by
# every block in docs/runbooks/build-the-estate.md.

# ---- Placement (variables.tf) ----
SUB=282207c6-4107-47fa-9d4e-b2fa9b3066cb            # subscription_id (IT-Production)
TENANT=75bd6050-92a8-4bde-a406-50000b310c86         # tenant_id
LOC=uksouth                                         # location
RG=zendesk-prod-rg-uksouth                          # resource_group_name
PREFIX=zendesk-prod                                 # name_prefix

# ---- Network (network.tf, ampls.tf, flowlogs.tf) ----
VNET=$PREFIX-vnet-uksouth
VNET_CIDR=10.81.0.0/23                              # vnet_address_space
SNET_APP=snet-app
SNET_APP_CIDR=10.81.0.64/27                         # cidrsubnet(vnet, 4, 2)
SNET_PE=snet-pe
SNET_PE_CIDR=10.81.0.32/28                          # cidrsubnet(vnet, 5, 2)
SNET_PE_MON=snet-pe-monitor
SNET_PE_MON_CIDR=10.81.0.96/27                      # cidrsubnet(vnet, 4, 3)
NAT_PIP=$PREFIX-natpip-uksouth
NATGW=$PREFIX-natgw-uksouth
DNS_VAULT=privatelink.vaultcore.azure.net
DNS_ACR=privatelink.azurecr.io
DNS_MONITOR="privatelink.monitor.azure.com privatelink.oms.opinsights.azure.com privatelink.ods.opinsights.azure.com privatelink.agentsvc.azure-automation.net privatelink.blob.core.windows.net"
FLOW_SA=zendeskprodflowuksouth                      # flow_log_storage_account_name
FLOWLOG=$PREFIX-vnetflow-uksouth
FLOWLOG_DAYS=30
NW_NAME=NetworkWatcher_uksouth
NW_RG=NetworkWatcherRG
NET_API=2024-05-01                                  # Microsoft.Network api-version for az rest

# ---- Monitoring (monitoring.tf, ampls.tf) ----
LAW=$PREFIX-law-uksouth
LAW_DAYS=30
APPI=$PREFIX-appi-uksouth
AMPLS=$PREFIX-ampls-uksouth
PE_AMPLS=$PREFIX-pe-ampls-uksouth
AMPLS_API=2021-07-01-preview                        # Microsoft.Insights/privateLinkScopes api-version

# ---- Key Vault (keyvault.tf) ----
KV=$PREFIX-kv-uksouth
PE_KV=$PREFIX-pe-kv-uksouth
KV_SOFT_DELETE_DAYS=90
KV_API=2023-07-01                                   # Microsoft.KeyVault api-version for az rest

# ---- Shared registry (acr.tf) ----
ACR=vitallyproducruksouth                           # acr_name
ACR_RG=vitally-prod-rg-uksouth                      # acr_resource_group_name
PE_ACR=$PREFIX-pe-acr-uksouth
ACR_ROLE_NAME="Zendesk MCP ACR importer"

# ---- Identity and CI/CD (identity.tf) ----
MI=$PREFIX-id-uksouth
GH_REPO=fiscaltec/zendesk-mcp
GHCR_IMAGE=ghcr.io/fiscaltec/zendesk-mcp            # deploy.yml: ghcr.io/${GITHUB_REPOSITORY,,}
FIC_ISSUER=https://token.actions.githubusercontent.com
FIC_AUDIENCE=api://AzureADTokenExchange

# ---- Container Apps (containerapps.tf, containerapps-staging.tf) ----
CAE=$PREFIX-cae-uksouth
APP_PROD=$PREFIX-ca-uksouth
APP_STG=zendesk-staging-ca-uksouth                  # staging_app_name
JOB=$PREFIX-secscan-uksouth
IMAGE_REPO=zendesk-mcp
IMAGE_TAG=                                          # image_tag — set in Phase 8
STAGING_IMAGE_TAG=                                  # staging_image_tag — set in Phase 8
SCANNER_IMAGE=python:3-slim
SCANNER_CRON="0 8 * * 1"

# ---- Zendesk (variables.tf) ----
ZD_SUBDOMAIN=fiscalsupport                          # zendesk_subdomain
ZD_CLIENT_ID=fiscal_zendesk_mcp                     # zendesk_oauth_client_id
PROD_ALLOW_PUBLIC_REPLIES=true                      # zendesk_allow_public_replies
PROD_ALLOW_ARTICLE_PUBLISHING=true                  # zendesk_allow_article_publishing
PROD_ALLOW_HC_STRUCTURE=true                        # zendesk_allow_help_center_structure_changes
PROD_ALLOW_MACRO_CHANGES=true                       # zendesk_allow_macro_changes
PROD_ALLOW_USER_ORG_CHANGES=true                    # zendesk_allow_user_and_organization_changes
STG_ALLOW_PUBLIC_REPLIES=true                       # staging_zendesk_allow_public_replies
STG_ALLOW_ARTICLE_PUBLISHING=true                   # staging_zendesk_allow_article_publishing
STG_ALLOW_HC_STRUCTURE=true                         # staging_zendesk_allow_help_center_structure_changes
STG_ALLOW_MACRO_CHANGES=true                        # staging_zendesk_allow_macro_changes
STG_ALLOW_USER_ORG_CHANGES=true                     # staging_zendesk_allow_user_and_organization_changes
STG_READ_ONLY=true                                  # staging_read_only

# ---- OAuth / Entra (variables.tf, entra.tf) ----
PROD_HOST=zendesk-mcp.fiscaltec.com
STG_HOST=zendesk-mcp-staging.fiscaltec.com
PROD_ORIGIN=https://$PROD_HOST                      # public_base_url
STG_ORIGIN=https://$STG_HOST                        # staging_public_base_url
OAUTH_AUTHORITY=https://login.microsoftonline.com/$TENANT/v2.0   # oauth_authority
OAUTH_AUDIENCE=$PROD_ORIGIN                         # oauth_audience — NO trailing slash
OAUTH_RESOURCE=$PROD_ORIGIN/                        # oauth_resource — WITH the slash
STG_OAUTH_RESOURCE=$STG_ORIGIN/                     # staging_oauth_resource — WITH the slash
OAUTH_SCOPE=$OAUTH_AUDIENCE/mcp.access              # oauth_upstream_resource_scope
ALLOWED_REDIRECT=https://claude.ai/api/mcp/auth_callback   # allowed_client_redirect_uri
ENTRA_APP_NAME="Zendesk MCP"
SCOPE_ID=c46bfcbe-1e71-4068-a6a8-6401ea221f85       # entra_mcp_access_scope_id
GRAPH_APPID=00000003-0000-0000-c000-000000000000
GRAPH_SCOPE_OPENID=37f7f235-527c-4136-accd-4a02d197296e
GRAPH_SCOPE_PROFILE=14dad69e-099b-42c9-810b-d002981feec1
GRAPH_SCOPE_EMAIL=64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0
GRAPH_SCOPE_OFFLINE=7427e0e9-2fba-42fe-b0c0-848c9e6a8182
DEFAULT_APP_ROLE=00000000-0000-0000-0000-000000000000
GATE1_GROUP_NAME="IT & Security Department"         # entra_gate1_group_object_ids key
GATE1_GROUP_ID=6ba9bf61-e959-4dd9-8a96-d477a35b0d03 # entra_gate1_group_object_ids value
GROUP_READERS=sg-zendesk-readers
GROUP_EDITORS=sg-zendesk-editors
GROUP_ADMINS=sg-zendesk-admins
TIER_INDIVIDUAL=dsearle@fiscaltec.com               # Terraform README, Decisions: holds every tier

# ---- Working locations ----
SECRET_DIR=/tmp/zendesk-estate-secrets              # NOT under ~ (Cloud Shell persists ~)
SECRET_DAYS=180                                     # expiry standard for both shared secrets
REPO_DIR=$HOME/zendesk-mcp                          # a clone of $GH_REPO, for scan/run.py and the scripts

# ---- Helpers ----
expect() {    # expect <what> <expected> <actual>
  if [ "$3" = "$2" ]; then printf 'OK   %s = %s\n' "$1" "$2"
  else printf 'FAIL %s: expected [%s], got [%s]\n' "$1" "$2" "${3:-<empty>}" >&2; return 1; fi
}
expect_ci() { expect "$1" "${2,,}" "${3,,}"; }         # case-insensitive (resource ids, enum casing)
nonempty() {  # nonempty <what> <value>
  if [ -n "$2" ]; then printf 'OK   %s = %s\n' "$1" "$2"
  else printf 'FAIL %s: empty\n' "$1" >&2; return 1; fi
}
fail() { printf 'FAIL %s\n' "$1" >&2; return 1; }
assert_context() {
  expect "subscription" "$SUB" "$(az account show --query id -o tsv)" || return 1
  expect "tenant" "$TENANT" "$(az account show --query tenantId -o tsv)" || return 1
}
retry() {     # retry <command…> — for Entra/RBAC replication lag after a create
  local n=0
  until "$@"; do
    n=$((n + 1)); [ "$n" -ge 8 ] && { fail "gave up after $n attempts: $*"; return 1; }
    echo "  … not yet (attempt $n), retrying in 15s" >&2; sleep 15
  done
}
wait_until() {  # wait_until <expected> <command…> — polls the command's stdout, ~10 minutes
  local want=$1 got="" i; shift
  for i in $(seq 1 40); do
    got=$("$@" 2>/dev/null || true); [ "$got" = "$want" ] && return 0; sleep 15
  done
  fail "timed out waiting for [$want] from: $* (last: ${got:-<empty>})"
}
capture_secret() {  # capture_secret <name> — run ON ITS OWN, never inside a pasted block
  local v
  ( umask 077; mkdir -p "$SECRET_DIR" )
  printf 'Paste %s (input hidden), then Enter: ' "$1" >&2
  IFS= read -rs v; echo >&2
  [ -n "$v" ] || { fail "nothing captured for $1"; return 1; }
  case "$v" in *"'"*) fail "$1 contains a single quote; the YAML below cannot carry it"; return 1;; esac
  ( umask 077; printf %s "$v" > "$SECRET_DIR/$1" )
  echo "captured $1 ($(printf %s "$v" | wc -c) characters)" >&2
}
link_name() {  # private DNS VNet link names, as network.tf and ampls.tf build them
  case "$1" in
    "$DNS_VAULT") echo link-vault ;;
    "$DNS_ACR")   echo link-acr ;;
    *) local s=${1#privatelink.}; echo "link-${s//./-}" ;;
  esac
}
ENVFILE
while IFS= read -r _line; do      # put back tags recorded in Phase 8, if any
  [ -n "$_line" ] && sed -i "s/^${_line%%=*}=.*/$_line/" ~/zendesk-estate.env
done <<< "$_keep_tags"
unset _keep_tags _line
source ~/zendesk-estate.env
az account set --subscription "$SUB"
assert_context
```

---

## Phase 0 — Prerequisites

### 0.1 Elevations (PIM), for the whole build

| Where | Role | Why |
|---|---|---|
| Azure, subscription IT-Production | **Owner** | creates the RG and role assignments; also covers `vitally-prod-rg-uksouth` (custom role, AcrPull, approving the private endpoint onto the shared registry) and `NetworkWatcherRG` (the flow log) |
| Entra | **Application Administrator** | the app registration and its service principal, Gate 1 assignment |
| Entra | **Groups Administrator** | the three `sg-zendesk-*` groups and their membership |
| Entra | **Privileged Role Administrator** | the Graph *application* permission `GroupMember.Read.All` for the managed identity, and tenant-wide admin consent (Phase 10) |

Activate them before starting and check they last long enough: a lapsed elevation part-way through
turns into `AuthorizationFailed` / `Authorization_RequestDenied` mid-phase. Run `az login` again in
Cloud Shell after activating, or the cached token lacks the new rights.

### 0.2 Zendesk OAuth client (Admin Centre, by hand)

Admin Centre → Apps and integrations → APIs → OAuth clients →
`fiscal_zendesk_mcp`:

- **Client kind: Confidential.** Remove `http://localhost:5099/zendesk/callback`.
- **Redirect URLs — both:** `https://zendesk-mcp.fiscaltec.com/zendesk/callback` and
  `https://zendesk-mcp-staging.fiscaltec.com/zendesk/callback`.
- Do **not** regenerate the secret yet — that is Phase 7, immediately before it is stored.

### 0.3 Cloud Shell set-up and pre-flight

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
az config set extension.use_dynamic_install=yes_without_prompt -o none
# `az monitor app-insights` (Phases 2, 4, 9) is an extension, not core: install it here, so a missing
# one fails pre-flight rather than half-way through Phase 2. Every other group used here is core.
az extension show --name application-insights -o none 2>/dev/null || az extension add --name application-insights -o none
# The containerapp *extension* replaces the core `az containerapp` commands Phase 9 was tested with
# (a stale preview one was found installed during the first build, #3). Remove it before building.
if az extension show --name containerapp -o none 2>/dev/null; then
  echo "The containerapp extension is installed: run 'az extension remove --name containerapp', then re-run this block"; exit 1
fi
# Resource providers the estate uses (Terraform's azurerm registers these itself).
for ns in Microsoft.App Microsoft.Network Microsoft.KeyVault Microsoft.ManagedIdentity \
          Microsoft.OperationalInsights Microsoft.Insights Microsoft.Storage; do
  # Only when needed: on a set-up subscription this is reads only, with no writes to fail part-way.
  [ "$(az provider show -n "$ns" --query registrationState -o tsv)" = Registered ] \
    || az provider register --namespace "$ns" --wait -o none
done
# A clone of the repo, for scan/run.py (Phase 9) and the verification scripts (Phase 14).
# Proves gh can reach THIS repo, not just that a login exists. Not `gh auth status`: in a Claude Code
# cloud session the proxy-injected token serves REST but fails that check (2026-10-28, #3).
gh api "repos/$GH_REPO" --silent || { echo "gh cannot reach $GH_REPO — run 'gh auth login' first, then re-run this block"; exit 1; }
# git, not `gh repo clone`: that is GraphQL underneath, which cloud sessions refuse. The repo is public.
[ -d "$REPO_DIR/.git" ] || git clone -q "https://github.com/$GH_REPO.git" "$REPO_DIR"
git -C "$REPO_DIR" fetch -q origin main && git -C "$REPO_DIR" checkout -q main && git -C "$REPO_DIR" pull -q --ff-only
)
```

**If `az extension add` cannot reach the extension index** (`aka.ms` refused, as from a Claude Code
cloud session on 2026-10-28, #3): the wheels host `azcliprod.blob.core.windows.net` may still be
reachable. Take the latest *stable* `application-insights` entry's `downloadUrl` and `sha256Digest`
from Microsoft's index on GitHub (`Azure/azure-cli-extensions`, `src/index.json`), download the
wheel, and install it only if the digest matches — `--source` skips the check `az` would otherwise
make:

```bash
( set -euo pipefail
v=1.2.3   # latest stable on 2026-10-28; read the index rather than trusting this
want=c941c232d04366636e1354cfe00708521013f88a5b1e74740d8047ec46d90599
w=/tmp/application_insights-$v-py2.py3-none-any.whl
curl -fsS -o "$w" "https://azcliprod.blob.core.windows.net/cli-extensions/application_insights-$v-py2.py3-none-any.whl"
[ "$(sha256sum "$w" | awk '{print $1}')" = "$want" ] || { echo "FAIL digest mismatch — not installed"; rm -f "$w"; exit 1; }
az extension add --source "$w" --yes -o none && rm -f "$w" && echo "OK   application-insights $v installed"
)
```

**Verify** — everything this build assumes already exists, and nothing it creates does yet.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
for ns in Microsoft.App Microsoft.Network Microsoft.KeyVault Microsoft.ManagedIdentity \
          Microsoft.OperationalInsights Microsoft.Insights Microsoft.Storage; do
  expect "provider $ns" Registered "$(az provider show -n "$ns" --query registrationState -o tsv)"
done
# The shared registry (Vitally's).
expect "ACR login server" "$ACR.azurecr.io" "$(az acr show -n "$ACR" -g "$ACR_RG" --query loginServer -o tsv)"
expect "ACR public network access" Disabled "$(az acr show -n "$ACR" -g "$ACR_RG" --query publicNetworkAccess -o tsv)"
# The Gate 1 department group, by object id AND name — provably the intended group.
expect "Gate 1 group name" "$GATE1_GROUP_NAME" "$(az ad group show --group "$GATE1_GROUP_ID" --query displayName -o tsv)"
# The regional Network Watcher the flow log hangs off.
expect "Network Watcher" "$NW_NAME" "$(az network watcher list --query "[?location=='$LOC'].name | [0]" -o tsv)"
expect "Network Watcher RG" "${NW_RG,,}" "$(az network watcher list --query "[?location=='$LOC'].resourceGroup | [0]" -o tsv | tr '[:upper:]' '[:lower:]')"
# Greenfield checks: the RG must not exist yet, the VNet range must be free and the global names
# unclaimed. On a RE-RUN after Phase 1 they would fail against this estate's own resources, so they
# run only while the RG is absent.
if [ "$(az group exists -n "$RG")" = false ]; then
  echo "OK   RG $RG absent (greenfield)"
  # The VNet range is free in this subscription (variables.tf says it was on 2026-10-28; re-check).
  az network vnet list --query "[].addressSpace.addressPrefixes[]" -o tsv > /tmp/zendesk-prefixes.txt
  python3 - "$VNET_CIDR" /tmp/zendesk-prefixes.txt <<'PY'
import ipaddress, sys
want = ipaddress.ip_network(sys.argv[1])
clash = [l.strip() for l in open(sys.argv[2]) if l.strip() and ipaddress.ip_network(l.strip()).overlaps(want)]
print(("FAIL %s overlaps %s" % (want, clash)) if clash else ("OK   %s overlaps no VNet in the subscription" % want))
sys.exit(1 if clash else 0)
PY
  # Global names free: no soft-deleted vault of this name (purge protection would block it for 90 days),
  # and the storage account name is available.
  expect "no soft-deleted vault named $KV" 0 "$(az keyvault list-deleted --query "length([?name=='$KV'])" -o tsv)"
  expect "storage name $FLOW_SA available" true "$(az storage account check-name -n "$FLOW_SA" --query nameAvailable -o tsv)"
else
  echo "NOTE $RG exists: this is a re-run, so the greenfield checks are skipped"
fi
nonempty "application-insights extension version" "$(az extension show --name application-insights --query version -o tsv 2>/dev/null || true)"
expect "containerapp extension absent (core commands only)" 0 "$(az extension list --query "length([?name=='containerapp'])" -o tsv)"
# Terraform's preconditions on the OAuth origins (entra.tf), so a mistyped env file fails here.
expect "audience = production origin, no slash" "$PROD_ORIGIN" "$OAUTH_AUDIENCE"
expect "production resource = origin + /" "$PROD_ORIGIN/" "$OAUTH_RESOURCE"
expect "staging resource = origin + /" "$STG_ORIGIN/" "$STG_OAUTH_RESOURCE"
expect "upstream scope = audience + /mcp.access" "$OAUTH_AUDIENCE/mcp.access" "$OAUTH_SCOPE"
[ -s "$REPO_DIR/infra/terraform/scan/run.py" ] || fail "repo clone has no scan/run.py"
echo "Phase 0 verified"
)
```

---

## Phase 1 — Resource group and network

`network.tf`, `ampls.tf` (the monitor subnet and zones), `flowlogs.tf`.

> **Why `snet-app` is created with `az rest`.** `az network vnet subnet create --delegations` names
> the delegation `0`; Terraform names it `aca`. A raw PUT keeps the record exact. The other subnets
> use plain `az`.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
az group show -n "$RG" -o none 2>/dev/null || az group create -n "$RG" -l "$LOC" -o none

az network vnet show -g "$RG" -n "$VNET" -o none 2>/dev/null \
  || az network vnet create -g "$RG" -n "$VNET" -l "$LOC" --address-prefixes "$VNET_CIDR" -o none

# NAT gateway + static public IP: deterministic egress for snet-app (env, apps and job).
az network public-ip show -g "$RG" -n "$NAT_PIP" -o none 2>/dev/null \
  || az network public-ip create -g "$RG" -n "$NAT_PIP" -l "$LOC" --sku Standard --allocation-method Static -o none
az network nat gateway show -g "$RG" -n "$NATGW" -o none 2>/dev/null \
  || az network nat gateway create -g "$RG" -n "$NATGW" -l "$LOC" --sku Standard \
       --public-ip-addresses "$NAT_PIP" -o none
NATGW_ID=$(az network nat gateway show -g "$RG" -n "$NATGW" --query id -o tsv)

# snet-app: delegated to Microsoft.App/environments (delegation name "aca"), NAT gateway attached.
if ! az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_APP" -o none 2>/dev/null; then
  body=$(cat <<EOF
{"properties":{"addressPrefix":"$SNET_APP_CIDR",
 "delegations":[{"name":"aca","properties":{"serviceName":"Microsoft.App/environments"}}],
 "natGateway":{"id":"$NATGW_ID"},
 "privateEndpointNetworkPolicies":"Disabled","privateLinkServiceNetworkPolicies":"Enabled"}}
EOF
)
  az rest --method put \
    --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Network/virtualNetworks/$VNET/subnets/$SNET_APP?api-version=$NET_API" \
    --body "$body" -o none
  # az rest does not wait for the async PUT; the next subnet write on this VNet would conflict.
  wait_until Succeeded az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_APP" --query provisioningState -o tsv
fi

# snet-pe (Key Vault + ACR endpoints) and snet-pe-monitor (AMPLS endpoint, ~13 IPs on its own).
az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_PE" -o none 2>/dev/null \
  || az network vnet subnet create -g "$RG" --vnet-name "$VNET" -n "$SNET_PE" \
       --address-prefixes "$SNET_PE_CIDR" --private-endpoint-network-policies Disabled -o none
az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_PE_MON" -o none 2>/dev/null \
  || az network vnet subnet create -g "$RG" --vnet-name "$VNET" -n "$SNET_PE_MON" \
       --address-prefixes "$SNET_PE_MON_CIDR" --private-endpoint-network-policies Disabled -o none

# Private DNS zones, each linked to THIS VNet only, registration off. Zones of the same names exist
# in the Vitally RG for its VNet; these stay here and are linked only here.
for z in "$DNS_VAULT" "$DNS_ACR" $DNS_MONITOR; do
  az network private-dns zone show -g "$RG" -n "$z" -o none 2>/dev/null \
    || az network private-dns zone create -g "$RG" -n "$z" -o none
  l=$(link_name "$z")
  az network private-dns link vnet show -g "$RG" -z "$z" -n "$l" -o none 2>/dev/null \
    || az network private-dns link vnet create -g "$RG" -z "$z" -n "$l" -v "$VNET" -e false -o none
done

# Flow logs: hardened storage account (default-deny, trusted-services bypass so Network Watcher can
# write), then the VNet flow log on the subscription's shared regional watcher. Traffic analytics off.
az storage account show -g "$RG" -n "$FLOW_SA" -o none 2>/dev/null \
  || az storage account create -g "$RG" -n "$FLOW_SA" -l "$LOC" \
       --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --https-only true \
       --allow-blob-public-access false --allow-cross-tenant-replication false \
       --public-network-access Enabled --default-action Deny --bypass AzureServices -o none
VNET_ID=$(az network vnet show -g "$RG" -n "$VNET" --query id -o tsv)
SA_ID=$(az storage account show -g "$RG" -n "$FLOW_SA" --query id -o tsv)
az network watcher flow-log show --location "$LOC" --name "$FLOWLOG" -o none 2>/dev/null \
  || az network watcher flow-log create --location "$LOC" --name "$FLOWLOG" \
       --vnet "$VNET_ID" --storage-account "$SA_ID" --retention "$FLOWLOG_DAYS" --enabled true -o none
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
expect "RG location" "$LOC" "$(az group show -n "$RG" --query location -o tsv)"
expect "VNet address space" "$VNET_CIDR" "$(az network vnet show -g "$RG" -n "$VNET" --query "join(' ', addressSpace.addressPrefixes)" -o tsv)"
sn() { az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$1" --query "$2" -o tsv; }
expect "$SNET_APP prefix" "$SNET_APP_CIDR" "$(sn "$SNET_APP" addressPrefix)"
expect "$SNET_APP delegation name" aca "$(sn "$SNET_APP" "delegations[0].name")"
expect "$SNET_APP delegation service" Microsoft.App/environments "$(sn "$SNET_APP" "delegations[0].serviceName")"
expect_ci "$SNET_APP NAT gateway" "$(az network nat gateway show -g "$RG" -n "$NATGW" --query id -o tsv)" "$(sn "$SNET_APP" natGateway.id)"
expect "$SNET_PE prefix" "$SNET_PE_CIDR" "$(sn "$SNET_PE" addressPrefix)"
expect "$SNET_PE PE network policies" Disabled "$(sn "$SNET_PE" privateEndpointNetworkPolicies)"
expect "$SNET_PE_MON prefix" "$SNET_PE_MON_CIDR" "$(sn "$SNET_PE_MON" addressPrefix)"
expect "$SNET_PE_MON PE network policies" Disabled "$(sn "$SNET_PE_MON" privateEndpointNetworkPolicies)"
expect "NAT PIP SKU" Standard "$(az network public-ip show -g "$RG" -n "$NAT_PIP" --query sku.name -o tsv)"
expect "NAT PIP allocation" Static "$(az network public-ip show -g "$RG" -n "$NAT_PIP" --query publicIPAllocationMethod -o tsv)"
expect_ci "NAT gateway public IP" "$(az network public-ip show -g "$RG" -n "$NAT_PIP" --query id -o tsv)" \
  "$(az network nat gateway show -g "$RG" -n "$NATGW" --query "publicIpAddresses[0].id" -o tsv)"
expect "NAT gateway SKU" Standard "$(az network nat gateway show -g "$RG" -n "$NATGW" --query sku.name -o tsv)"
nonempty "NAT egress IP (record it: Terraform output nat_egress_ip)" "$(az network public-ip show -g "$RG" -n "$NAT_PIP" --query ipAddress -o tsv)"
for z in "$DNS_VAULT" "$DNS_ACR" $DNS_MONITOR; do
  l=$(link_name "$z")
  expect_ci "zone $z link $l VNet" "$(az network vnet show -g "$RG" -n "$VNET" --query id -o tsv)" \
    "$(az network private-dns link vnet show -g "$RG" -z "$z" -n "$l" --query virtualNetwork.id -o tsv)"
  expect "zone $z link $l registration" false "$(az network private-dns link vnet show -g "$RG" -z "$z" -n "$l" --query registrationEnabled -o tsv)"
done
sa() { az storage account show -g "$RG" -n "$FLOW_SA" --query "$1" -o tsv; }
expect "flow SA sku" Standard_LRS "$(sa sku.name)"
expect "flow SA kind" StorageV2 "$(sa kind)"
expect "flow SA TLS" TLS1_2 "$(sa minimumTlsVersion)"
expect "flow SA https only" true "$(sa enableHttpsTrafficOnly)"
expect "flow SA blob public access" false "$(sa allowBlobPublicAccess)"
expect "flow SA default action" Deny "$(sa networkRuleSet.defaultAction)"
expect "flow SA bypass" AzureServices "$(sa networkRuleSet.bypass)"
fl() { az network watcher flow-log show --location "$LOC" --name "$FLOWLOG" --query "$1" -o tsv; }
expect_ci "flow log target" "$(az network vnet show -g "$RG" -n "$VNET" --query id -o tsv)" "$(fl targetResourceId)"
expect_ci "flow log storage" "$(az storage account show -g "$RG" -n "$FLOW_SA" --query id -o tsv)" "$(fl storageId)"
expect "flow log enabled" true "$(fl enabled)"
expect "flow log retention enabled" true "$(fl retentionPolicy.enabled)"
expect "flow log retention days" "$FLOWLOG_DAYS" "$(fl retentionPolicy.days)"
expect "flow log watcher RG" "${NW_RG,,}" "$(fl resourceGroup | tr '[:upper:]' '[:lower:]')"
echo "Phase 1 verified"
)
```

---

## Phase 2 — Log Analytics, Application Insights, AMPLS

`monitoring.tf`, `ampls.tf`.

> ⚠️ **Local authentication off is load-bearing on both resources.** The workspace refuses
> shared-key writes, which is why the Container Apps environment must use the `azure-monitor` log
> destination (Phase 9); do not rescue that path by enabling local auth. On Application Insights it
> stops the connection string being a write credential for the audit trail. Check both with
> `az resource show` — the app-insights extension has been seen to report `DisableLocalAuth` as null
> while ARM reports true (`identity.tf`).
>
> ⚠️ **AMPLS access modes are not the public-access control.** `PrivateOnly` ingestion / `Open` query
> govern traffic *from networks connected through the endpoint*. Public ingestion is closed by
> `--ingestion-access Disabled` on each resource, below. Diagnostic-setting delivery is governed by
> neither (vitally-mcp#142).

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
az monitor log-analytics workspace show -g "$RG" -n "$LAW" -o none 2>/dev/null \
  || az monitor log-analytics workspace create -g "$RG" -n "$LAW" -l "$LOC" --sku PerGB2018 \
       --retention-time "$LAW_DAYS" --ingestion-access Disabled --query-access Enabled -o none
LAW_ID=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)
az resource update --ids "$LAW_ID" --set properties.features.disableLocalAuth=true -o none

az monitor app-insights component show --app "$APPI" -g "$RG" -o none 2>/dev/null \
  || az monitor app-insights component create --app "$APPI" -g "$RG" -l "$LOC" --kind web \
       --application-type web --workspace "$LAW_ID" --ingestion-access Disabled --query-access Enabled -o none
APPI_ID=$(az monitor app-insights component show --app "$APPI" -g "$RG" --query id -o tsv)
az resource update --ids "$APPI_ID" --set properties.DisableLocalAuth=true -o none

# AMPLS with its access modes — az's private-link-scope create has no flags for them, so a raw PUT.
az rest --method put \
  --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Insights/privateLinkScopes/$AMPLS?api-version=$AMPLS_API" \
  --body '{"location":"global","properties":{"accessModeSettings":{"ingestionAccessMode":"PrivateOnly","queryAccessMode":"Open"}}}' -o none
az monitor private-link-scope scoped-resource show -g "$RG" --scope-name "$AMPLS" -n law-link -o none 2>/dev/null \
  || az monitor private-link-scope scoped-resource create -g "$RG" --scope-name "$AMPLS" -n law-link --linked-resource "$LAW_ID" -o none
az monitor private-link-scope scoped-resource show -g "$RG" --scope-name "$AMPLS" -n appi-link -o none 2>/dev/null \
  || az monitor private-link-scope scoped-resource create -g "$RG" --scope-name "$AMPLS" -n appi-link --linked-resource "$APPI_ID" -o none
AMPLS_ID=$(az monitor private-link-scope show -g "$RG" -n "$AMPLS" --query id -o tsv)

# Private endpoint in snet-pe-monitor, binding all five zones so the A records populate.
az network private-endpoint show -g "$RG" -n "$PE_AMPLS" -o none 2>/dev/null \
  || az network private-endpoint create -g "$RG" -n "$PE_AMPLS" -l "$LOC" --vnet-name "$VNET" --subnet "$SNET_PE_MON" \
       --private-connection-resource-id "$AMPLS_ID" --group-id azuremonitor --connection-name "$PE_AMPLS-conn" -o none
set -- $DNS_MONITOR
# list, not show: `dns-zone-group show` answers {} with exit 0 for a group that does not exist.
[ "$(az network private-endpoint dns-zone-group list -g "$RG" --endpoint-name "$PE_AMPLS" --query "length([?name=='ampls-dns-group'])" -o tsv)" = 1 ] \
  || az network private-endpoint dns-zone-group create -g "$RG" --endpoint-name "$PE_AMPLS" -n ampls-dns-group \
       --zone-name "$1" --private-dns-zone "$(az network private-dns zone show -g "$RG" -n "$1" --query id -o tsv)" -o none
for z in $DNS_MONITOR; do
  have=$(az network private-endpoint dns-zone-group show -g "$RG" --endpoint-name "$PE_AMPLS" -n ampls-dns-group \
           --query "length(privateDnsZoneConfigs[?name=='$z'])" -o tsv)
  [ "$have" = 1 ] || az network private-endpoint dns-zone-group add -g "$RG" --endpoint-name "$PE_AMPLS" -n ampls-dns-group \
       --zone-name "$z" --private-dns-zone "$(az network private-dns zone show -g "$RG" -n "$z" --query id -o tsv)" -o none
done
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
LAW_ID=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)
APPI_ID=$(az monitor app-insights component show --app "$APPI" -g "$RG" --query id -o tsv)
r() { az resource show --ids "$1" --query "$2" -o tsv; }
expect "LAW sku" PerGB2018 "$(r "$LAW_ID" properties.sku.name)"
expect "LAW retention" "$LAW_DAYS" "$(r "$LAW_ID" properties.retentionInDays)"
expect "LAW public ingestion" Disabled "$(r "$LAW_ID" properties.publicNetworkAccessForIngestion)"
expect "LAW public query" Enabled "$(r "$LAW_ID" properties.publicNetworkAccessForQuery)"
expect "LAW disableLocalAuth (ARM)" true "$(r "$LAW_ID" properties.features.disableLocalAuth)"
expect "AppI DisableLocalAuth (ARM, not the extension)" true "$(r "$APPI_ID" properties.DisableLocalAuth)"
expect "AppI public ingestion" Disabled "$(r "$APPI_ID" properties.publicNetworkAccessForIngestion)"
expect "AppI public query" Enabled "$(r "$APPI_ID" properties.publicNetworkAccessForQuery)"
expect "AppI application type" web "$(r "$APPI_ID" properties.Application_Type)"
expect "AppI kind" web "$(r "$APPI_ID" kind)"
expect_ci "AppI workspace" "$LAW_ID" "$(r "$APPI_ID" properties.WorkspaceResourceId)"
nonempty "AppI connection string" "$(az monitor app-insights component show --app "$APPI" -g "$RG" --query connectionString -o tsv)"
AMPLS_ID=$(az monitor private-link-scope show -g "$RG" -n "$AMPLS" --query id -o tsv)
expect "AMPLS ingestion mode" PrivateOnly "$(r "$AMPLS_ID" properties.accessModeSettings.ingestionAccessMode)"
expect "AMPLS query mode" Open "$(r "$AMPLS_ID" properties.accessModeSettings.queryAccessMode)"
expect_ci "AMPLS law-link" "$LAW_ID" "$(az monitor private-link-scope scoped-resource show -g "$RG" --scope-name "$AMPLS" -n law-link --query linkedResourceId -o tsv)"
expect_ci "AMPLS appi-link" "$APPI_ID" "$(az monitor private-link-scope scoped-resource show -g "$RG" --scope-name "$AMPLS" -n appi-link --query linkedResourceId -o tsv)"
pe() { az network private-endpoint show -g "$RG" -n "$PE_AMPLS" --query "$1" -o tsv; }
expect "AMPLS PE connection" Approved "$(pe "privateLinkServiceConnections[0].privateLinkServiceConnectionState.status")"
expect "AMPLS PE connection name" "$PE_AMPLS-conn" "$(pe "privateLinkServiceConnections[0].name")"
expect "AMPLS PE group" azuremonitor "$(pe "privateLinkServiceConnections[0].groupIds[0]")"
expect_ci "AMPLS PE subnet" "$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_PE_MON" --query id -o tsv)" "$(pe subnet.id)"
expect "AMPLS DNS zone configs" 5 "$(az network private-endpoint dns-zone-group show -g "$RG" --endpoint-name "$PE_AMPLS" -n ampls-dns-group --query "length(privateDnsZoneConfigs)" -o tsv)"
for z in $DNS_MONITOR; do
  n=$(az network private-dns record-set a list -g "$RG" -z "$z" --query "length(@)" -o tsv)
  [ "${n:-0}" -ge 1 ] && echo "OK   $z has $n A record set(s)" || fail "$z has no A records"
done
echo "Phase 2 verified"
)
```

---

## Phase 3 — Key Vault

`keyvault.tf`. RBAC authorisation, purge protection, 90-day soft delete, public access off from the
start, private endpoint only. Holds `zendesk-oauth-client-secret`, `entra-mcp-client-secret` and one
secret per connected person, which the app writes itself.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
az keyvault show -g "$RG" -n "$KV" -o none 2>/dev/null \
  || az keyvault create -g "$RG" -n "$KV" -l "$LOC" --sku standard --enable-rbac-authorization true \
       --enable-purge-protection true --retention-days "$KV_SOFT_DELETE_DAYS" --public-network-access Disabled -o none
KV_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)

az network private-endpoint show -g "$RG" -n "$PE_KV" -o none 2>/dev/null \
  || az network private-endpoint create -g "$RG" -n "$PE_KV" -l "$LOC" --vnet-name "$VNET" --subnet "$SNET_PE" \
       --private-connection-resource-id "$KV_ID" --group-id vault --connection-name kv-conn -o none
# list, not show: `dns-zone-group show` answers {} with exit 0 for a group that does not exist.
[ "$(az network private-endpoint dns-zone-group list -g "$RG" --endpoint-name "$PE_KV" --query "length([?name=='kv-zg'])" -o tsv)" = 1 ] \
  || az network private-endpoint dns-zone-group create -g "$RG" --endpoint-name "$PE_KV" -n kv-zg \
       --zone-name "$DNS_VAULT" --private-dns-zone "$(az network private-dns zone show -g "$RG" -n "$DNS_VAULT" --query id -o tsv)" -o none

# AuditEvent: every read and write of every secret, per-person tokens included.
az monitor diagnostic-settings show -n to-law --resource "$KV_ID" -o none 2>/dev/null \
  || az monitor diagnostic-settings create -n to-law --resource "$KV_ID" \
       --workspace "$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)" \
       --logs '[{"category":"AuditEvent","enabled":true},{"category":"AzurePolicyEvaluationDetails","enabled":true}]' \
       --metrics '[{"category":"AllMetrics","enabled":true}]' -o none
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
kv() { az keyvault show -g "$RG" -n "$KV" --query "$1" -o tsv; }
KV_ID=$(kv id)
expect "KV sku" standard "$(kv properties.sku.name)"
expect "KV tenant" "$TENANT" "$(kv properties.tenantId)"
expect "KV RBAC authorisation" true "$(kv properties.enableRbacAuthorization)"
expect "KV purge protection" true "$(kv properties.enablePurgeProtection)"
expect "KV soft-delete days" "$KV_SOFT_DELETE_DAYS" "$(kv properties.softDeleteRetentionInDays)"
expect "KV public network access" Disabled "$(kv properties.publicNetworkAccess)"
expect "KV IP rules" 0 "$(kv "length(properties.networkAcls.ipRules || \`[]\`)")"
nonempty "KV URI" "$(kv properties.vaultUri)"
pe() { az network private-endpoint show -g "$RG" -n "$PE_KV" --query "$1" -o tsv; }
expect "KV PE connection" Approved "$(pe "privateLinkServiceConnections[0].privateLinkServiceConnectionState.status")"
expect "KV PE connection name" kv-conn "$(pe "privateLinkServiceConnections[0].name")"
expect "KV PE group" vault "$(pe "privateLinkServiceConnections[0].groupIds[0]")"
expect_ci "KV PE subnet" "$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_PE" --query id -o tsv)" "$(pe subnet.id)"
expect "KV private A record" 1 "$(az network private-dns record-set a list -g "$RG" -z "$DNS_VAULT" --query "length([?name=='$KV'])" -o tsv)"
ds() { az monitor diagnostic-settings show -n to-law --resource "$KV_ID" --query "$1" -o tsv; }
expect "KV diag logs" "AuditEvent,AzurePolicyEvaluationDetails" "$(ds "join(',', sort(logs[?enabled].category))")"
expect "KV diag metrics" AllMetrics "$(ds "join(',', metrics[?enabled].category)")"
expect_ci "KV diag workspace" "$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)" "$(ds workspaceId)"
echo "Phase 3 verified"
)
```

---

## Phase 4 — Managed identity, federated credentials, role assignments

`identity.tf`. One user-assigned identity is the runtime identity of both apps and the Job **and**
the GitHub deploy identity. The two app-scoped `Contributor` assignments need the apps, so they are
in Phase 9; the Graph application permission is Phase 10.

> ⚠️ The custom role exists because `AcrPush` lacks `importImage` and `Contributor` would let this
> identity delete or reconfigure the **Vitally** registry. What remains, accepted with the shared
> registry: `az acr import --force` can overwrite any tag in it, Vitally's included (`acr.tf`).

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
az identity show -g "$RG" -n "$MI" -o none 2>/dev/null || az identity create -g "$RG" -n "$MI" -l "$LOC" -o none
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)

# One federated credential per GitHub environment — the subject is an exact match.
for t in prod:production staging:staging; do
  n="github-actions-${t%%:*}-env"; subj="repo:$GH_REPO:environment:${t#*:}"
  az identity federated-credential show -g "$RG" --identity-name "$MI" -n "$n" -o none 2>/dev/null \
    || az identity federated-credential create -g "$RG" --identity-name "$MI" -n "$n" \
         --issuer "$FIC_ISSUER" --subject "$subj" --audiences "$FIC_AUDIENCE" -o none
done

assign() {  # assign <role name or id> <scope>
  local have
  have=$(az role assignment list --scope "$2" --assignee-object-id "$MI_PID" \
           --query "length([?roleDefinitionName=='$1' || ends_with(roleDefinitionId, '/$1')])" -o tsv)
  [ "$have" = 0 ] || { echo "present: $1 on ${2##*/}"; return 0; }
  retry az role assignment create --assignee-object-id "$MI_PID" --assignee-principal-type ServicePrincipal \
    --role "$1" --scope "$2" -o none
}
KV_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
ACR_ID=$(az acr show -g "$ACR_RG" -n "$ACR" --query id -o tsv)
APPI_ID=$(az monitor app-insights component show --app "$APPI" -g "$RG" --query id -o tsv)
RG_ID=$(az group show -n "$RG" --query id -o tsv)
assign "Key Vault Secrets Officer"    "$KV_ID"     # app writes per-person token secrets
assign "AcrPull"                      "$ACR_ID"    # pull from the shared registry
assign "Monitoring Metrics Publisher" "$APPI_ID"   # exporter auth — the component has local auth off
assign "ContainerApp Reader"          "$RG_ID"     # release train tells absent staging from a failed lookup

# Custom import-only role on the shared registry, then its assignment.
if [ "$(az role definition list --custom-role-only true --scope "$ACR_ID" --query "length([?roleName=='$ACR_ROLE_NAME'])" -o tsv)" = 0 ]; then
  cat > /tmp/zendesk-acr-role.json <<EOF
{"Name":"$ACR_ROLE_NAME",
 "Description":"Import images into the shared registry (az acr import) and nothing else. Assigned to the Zendesk MCP identity.",
 "Actions":["Microsoft.ContainerRegistry/registries/read","Microsoft.ContainerRegistry/registries/importImage/action"],
 "NotActions":[],"DataActions":[],"NotDataActions":[],
 "AssignableScopes":["$ACR_ID"]}
EOF
  az role definition create --role-definition @/tmp/zendesk-acr-role.json -o none
fi
ROLE_ID=$(retry az role definition list --custom-role-only true --scope "$ACR_ID" --query "[?roleName=='$ACR_ROLE_NAME'].name | [0]" -o tsv)
nonempty "custom role id" "$ROLE_ID"
assign "$ROLE_ID" "$ACR_ID"
)
```

`retry` around `role definition list` only retries a *failing* command; if the list comes back
empty right after creation, the `nonempty` check stops the block — wait a minute and re-run it (the
create is guarded).

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)
nonempty "MI principal id (Terraform output managed_identity_principal_id)" "$MI_PID"
nonempty "MI client id (Terraform output managed_identity_client_id)" "$(az identity show -g "$RG" -n "$MI" --query clientId -o tsv)"
for t in prod:production staging:staging; do
  n="github-actions-${t%%:*}-env"
  fc() { az identity federated-credential show -g "$RG" --identity-name "$MI" -n "$n" --query "$1" -o tsv; }
  expect "$n subject" "repo:$GH_REPO:environment:${t#*:}" "$(fc subject)"
  expect "$n issuer" "$FIC_ISSUER" "$(fc issuer)"
  expect "$n audience" "$FIC_AUDIENCE" "$(fc "join(',', audiences)")"
done
has() { az role assignment list --scope "$2" --assignee-object-id "$MI_PID" --query "length([?roleDefinitionName=='$1'])" -o tsv; }
expect "Key Vault Secrets Officer on vault" 1 "$(has "Key Vault Secrets Officer" "$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)")"
ACR_ID=$(az acr show -g "$ACR_RG" -n "$ACR" --query id -o tsv)
expect "AcrPull on shared registry" 1 "$(has AcrPull "$ACR_ID")"
expect "importer role on shared registry" 1 "$(has "$ACR_ROLE_NAME" "$ACR_ID")"
expect "Monitoring Metrics Publisher on AppI" 1 "$(has "Monitoring Metrics Publisher" "$(az monitor app-insights component show --app "$APPI" -g "$RG" --query id -o tsv)")"
expect "ContainerApp Reader on RG" 1 "$(has "ContainerApp Reader" "$(az group show -n "$RG" --query id -o tsv)")"
rd() { az role definition list --custom-role-only true --scope "$ACR_ID" --query "[?roleName=='$ACR_ROLE_NAME'] | [0].$1" -o tsv; }
expect "importer role actions" "Microsoft.ContainerRegistry/registries/importImage/action,Microsoft.ContainerRegistry/registries/read" \
  "$(rd "join(',', sort(permissions[0].actions))")"
expect_ci "importer role assignable scope" "$ACR_ID" "$(rd "join(',', assignableScopes)")"
echo "Phase 4 verified"
)
```

---

## Phase 5 — Private endpoint onto the shared registry

`acr.tf`. A second private endpoint onto Vitally's registry, from this VNet, registering in this
estate's `privatelink.azurecr.io` zone. Auto-approved because you hold Owner over the registry; if it
lands `Pending`, someone with rights on `vitally-prod-rg-uksouth` must approve it.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
ACR_ID=$(az acr show -g "$ACR_RG" -n "$ACR" --query id -o tsv)
az network private-endpoint show -g "$RG" -n "$PE_ACR" -o none 2>/dev/null \
  || az network private-endpoint create -g "$RG" -n "$PE_ACR" -l "$LOC" --vnet-name "$VNET" --subnet "$SNET_PE" \
       --private-connection-resource-id "$ACR_ID" --group-id registry --connection-name acr-conn -o none
# list, not show: `dns-zone-group show` answers {} with exit 0 for a group that does not exist.
[ "$(az network private-endpoint dns-zone-group list -g "$RG" --endpoint-name "$PE_ACR" --query "length([?name=='acr-zg'])" -o tsv)" = 1 ] \
  || az network private-endpoint dns-zone-group create -g "$RG" --endpoint-name "$PE_ACR" -n acr-zg \
       --zone-name "$DNS_ACR" --private-dns-zone "$(az network private-dns zone show -g "$RG" -n "$DNS_ACR" --query id -o tsv)" -o none
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
pe() { az network private-endpoint show -g "$RG" -n "$PE_ACR" --query "$1" -o tsv; }
expect "ACR PE connection" Approved "$(pe "privateLinkServiceConnections[0].privateLinkServiceConnectionState.status")"
expect "ACR PE connection name" acr-conn "$(pe "privateLinkServiceConnections[0].name")"
expect "ACR PE group" registry "$(pe "privateLinkServiceConnections[0].groupIds[0]")"
expect_ci "ACR PE target" "$(az acr show -g "$ACR_RG" -n "$ACR" --query id -o tsv)" "$(pe "privateLinkServiceConnections[0].privateLinkServiceId")"
# Two records: the registry and its regional data endpoint.
expect "ACR registry A record" 1 "$(az network private-dns record-set a list -g "$RG" -z "$DNS_ACR" --query "length([?name=='$ACR'])" -o tsv)"
expect "ACR data A record" 1 "$(az network private-dns record-set a list -g "$RG" -z "$DNS_ACR" --query "length([?name=='$ACR.$LOC.data'])" -o tsv)"
echo "Phase 5 verified"
)
```

---

## Phase 6 — Entra: app registration, service principal, Gate 1, tier groups

`entra.tf`. One registration is both the OAuth client and the API resource, for both origins.

> ⚠️ **Two ordering constraints — both hit for real in vitally-mcp, and `az` hides neither:**
>
> 1. **`api.requestedAccessTokenVersion = 2` before `identifierUris`.** The tenant's
>    `defaultAppManagementPolicy` would otherwise force the `https://fiscaltec.com/{guid}` shape. So
>    the app is created with the version set (6.1), and the URI is added in a **separate** PATCH (6.2).
> 2. **A scope cannot be pre-authorised in the write that creates it** — Graph rejects the whole
>    request with `InvalidValue … has a Permission Id that cannot be found in the AppPermissions
>    sets`. So the scope (6.3) and the pre-authorisation (6.5) are separate PATCHes.
>
> Each `api` PATCH below carries the **whole** `api` object (version, scope, and later the
> pre-authorisation), so it does not matter whether Graph merges or replaces that complex property.
>
> `OAuth__Audience` (no slash) and `OAuth__Resource` (with the slash) differ by one character **on
> purpose** — Entra refuses a trailing slash on an identifier URI. Do not reconcile them.

### 6.1–6.6 The registration

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
ME=$(az ad signed-in-user show --query id -o tsv)
G=https://graph.microsoft.com/v1.0
n=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "length([?displayName=='$ENTRA_APP_NAME'])" -o tsv)
[ "$n" -le 1 ] || { fail "$n apps are named '$ENTRA_APP_NAME' — resolve by hand before continuing"; exit 1; }

# 6.1 Create — version 2 FIRST, no identifier URI yet. Web redirect URIs, implicit grant off, upn claim.
if [ "$n" = 0 ]; then
  body=$(cat <<EOF
{"displayName":"$ENTRA_APP_NAME","signInAudience":"AzureADMyOrg",
 "notes":"OAuth client + API resource for the Zendesk MCP server (github.com/$GH_REPO). Managed by infra/terraform/entra.tf.",
 "api":{"requestedAccessTokenVersion":2},
 "web":{"redirectUris":["$PROD_ORIGIN/oauth/callback","$STG_ORIGIN/oauth/callback"],
        "implicitGrantSettings":{"enableAccessTokenIssuance":false,"enableIdTokenIssuance":false}},
 "optionalClaims":{"accessToken":[{"name":"upn","essential":false,"additionalProperties":[]}],"idToken":[],"saml2Token":[]}}
EOF
)
  az rest --method post --url "$G/applications" --body "$body" -o none
fi
APP_OBJ=$(retry az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].id" -o tsv)
nonempty "app object id" "$APP_OBJ"
APP_ID=$(az ad app show --id "$APP_OBJ" --query appId -o tsv)

# 6.2 Identifier URI — a SEPARATE write, after the version (constraint 1).
retry az rest --method patch --url "$G/applications/$APP_OBJ" --body "{\"identifierUris\":[\"$OAUTH_AUDIENCE\"]}" -o none

# 6.3 The mcp.access scope, with its fixed id (changing it invalidates every consent grant).
SCOPE=$(cat <<EOF
{"id":"$SCOPE_ID","value":"mcp.access","type":"User","isEnabled":true,
 "adminConsentDisplayName":"Access the Zendesk MCP server",
 "adminConsentDescription":"Allows the signed-in user to call the Zendesk MCP server on their behalf. Tool-level entitlement is resolved separately from sg-zendesk-* group membership, and every Zendesk call runs as the user's own connected Zendesk account.",
 "userConsentDisplayName":"Access Zendesk MCP",
 "userConsentDescription":"Allows you to use the Zendesk MCP server on your behalf."}
EOF
)
az rest --method patch --url "$G/applications/$APP_OBJ" \
  --body "{\"api\":{\"requestedAccessTokenVersion\":2,\"oauth2PermissionScopes\":[$SCOPE]}}" -o none

# 6.4 API permissions: Graph's delegated OIDC scopes, and this app's own mcp.access (the request that
#     binds the token's audience, since the proxy terminates RFC 8707 `resource`).
az rest --method patch --url "$G/applications/$APP_OBJ" --body "$(cat <<EOF
{"requiredResourceAccess":[
 {"resourceAppId":"$GRAPH_APPID","resourceAccess":[
   {"id":"$GRAPH_SCOPE_OPENID","type":"Scope"},{"id":"$GRAPH_SCOPE_PROFILE","type":"Scope"},
   {"id":"$GRAPH_SCOPE_EMAIL","type":"Scope"},{"id":"$GRAPH_SCOPE_OFFLINE","type":"Scope"}]},
 {"resourceAppId":"$APP_ID","resourceAccess":[{"id":"$SCOPE_ID","type":"Scope"}]}]}
EOF
)" -o none

# 6.5 Pre-authorise the app for its own scope — a SEPARATE write from the one that created it
#     (constraint 2). Suppresses the consent screen; it is NOT what authenticates the token exchange.
az rest --method patch --url "$G/applications/$APP_OBJ" \
  --body "{\"api\":{\"requestedAccessTokenVersion\":2,\"oauth2PermissionScopes\":[$SCOPE],\"preAuthorizedApplications\":[{\"appId\":\"$APP_ID\",\"delegatedPermissionIds\":[\"$SCOPE_ID\"]}]}}" -o none

# Owner: the person building it (Terraform: the applying principal).
[ "$(az ad app owner list --id "$APP_OBJ" --query "length([?id=='$ME'])" -o tsv)" = 1 ] \
  || az ad app owner add --id "$APP_OBJ" --owner-object-id "$ME" -o none

# 6.6 Service principal, Gate 1 on (sign-in restricted to assigned principals), owner.
az ad sp show --id "$APP_ID" -o none 2>/dev/null || retry az ad sp create --id "$APP_ID" -o none
SP_ID=$(retry az ad sp show --id "$APP_ID" --query id -o tsv)
az rest --method patch --url "$G/servicePrincipals/$SP_ID" --body '{"appRoleAssignmentRequired":true}' -o none
[ "$(az rest --method get --url "$G/servicePrincipals/$SP_ID/owners" --query "length(value[?id=='$ME'])" -o tsv)" = 1 ] \
  || az rest --method post --url "$G/servicePrincipals/$SP_ID/owners/\$ref" \
       --body "{\"@odata.id\":\"$G/directoryObjects/$ME\"}" -o none
)
```

### 6.7 Gate 1 assignment and the tier groups

> ⚠️ **Gate 1 must be a DIRECT assignment of the department group.** The app-assignment gate ignores
> nesting, so assigning a `sg-zendesk-*` group would admit only its direct members. That assignment
> list is the **only** record of who can sign in; an omission is that department's outage at its next
> sign-in (`AADSTS50105`). Gate 2 (tiers) is separate: the server reads `sg-zendesk-*` membership
> transitively through Graph.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
ME=$(az ad signed-in-user show --query id -o tsv)
G=https://graph.microsoft.com/v1.0
APP_ID=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)
SP_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)
nonempty "service principal" "$SP_ID"

have=$(az rest --method get --url "$G/servicePrincipals/$SP_ID/appRoleAssignedTo?\$top=999" \
         --query "length(value[?principalId=='$GATE1_GROUP_ID' && appRoleId=='$DEFAULT_APP_ROLE'])" -o tsv)
[ "$have" = 1 ] || az rest --method post --url "$G/servicePrincipals/$SP_ID/appRoleAssignedTo" \
  --body "{\"principalId\":\"$GATE1_GROUP_ID\",\"resourceId\":\"$SP_ID\",\"appRoleId\":\"$DEFAULT_APP_ROLE\"}" -o none

# Tier groups: security, not mail-enabled, not role-assignable; names must be unique. Membership is
# NOT set here (Phase 12) — it is administered outside any build.
mk() {  # mk <name> <description>
  local n
  n=$(az ad group list --display-name "$1" --query "length([?displayName=='$1'])" -o tsv)
  [ "$n" -le 1 ] || { fail "$n groups are named $1"; return 1; }
  [ "$n" = 1 ] || az ad group create --display-name "$1" --mail-nickname "$1" --description "$2" -o none
  local id; id=$(retry az ad group show --group "$1" --query id -o tsv)
  [ "$(az ad group owner list --group "$id" --query "length([?id=='$ME'])" -o tsv)" = 1 ] \
    || az ad group owner add --group "$id" --owner-object-id "$ME" -o none
}
mk "$GROUP_READERS" "Zendesk MCP server: read tools. Membership read transitively by the server (Gate 2); sign-in is Gate 1, on the app."
mk "$GROUP_EDITORS" "Zendesk MCP server: read and create/update tools."
mk "$GROUP_ADMINS"  "Zendesk MCP server: every tool, including deletes."
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
ME=$(az ad signed-in-user show --query id -o tsv)
G=https://graph.microsoft.com/v1.0
expect "apps named '$ENTRA_APP_NAME'" 1 "$(az ad app list --display-name "$ENTRA_APP_NAME" --query "length([?displayName=='$ENTRA_APP_NAME'])" -o tsv)"
APP_ID=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)
a() { az ad app show --id "$APP_ID" --query "$1" -o tsv; }
expect "sign-in audience" AzureADMyOrg "$(a signInAudience)"
expect "identifier URIs (no trailing slash)" "$OAUTH_AUDIENCE" "$(a "join(',', identifierUris)")"
expect "access token version" 2 "$(a api.requestedAccessTokenVersion)"
expect "scope" "$SCOPE_ID mcp.access User true" "$(a "api.oauth2PermissionScopes[?id=='$SCOPE_ID'] | [0].join(' ', [id, value, type, to_string(isEnabled)])")"
expect "pre-authorised client" "$APP_ID" "$(a "api.preAuthorizedApplications[0].appId")"
expect "pre-authorised scope" "$SCOPE_ID" "$(a "join(',', api.preAuthorizedApplications[0].delegatedPermissionIds)")"
expect "optional claim upn on access token" upn "$(a "join(',', optionalClaims.accessToken[].name)")"
expect "redirect URIs" "$(printf '%s\n' "$PROD_ORIGIN/oauth/callback" "$STG_ORIGIN/oauth/callback" | sort | paste -sd,)" "$(a "join(',', sort(web.redirectUris))")"
expect "implicit access tokens" false "$(a web.implicitGrantSettings.enableAccessTokenIssuance)"
expect "implicit id tokens" false "$(a web.implicitGrantSettings.enableIdTokenIssuance)"
expect "Graph scopes requested" "$(printf '%s\n' "$GRAPH_SCOPE_OPENID" "$GRAPH_SCOPE_PROFILE" "$GRAPH_SCOPE_EMAIL" "$GRAPH_SCOPE_OFFLINE" | sort | paste -sd,)" \
  "$(a "join(',', sort(requiredResourceAccess[?resourceAppId=='$GRAPH_APPID'] | [0].resourceAccess[].id))")"
expect "own scope requested" "$SCOPE_ID" "$(a "join(',', requiredResourceAccess[?resourceAppId=='$APP_ID'] | [0].resourceAccess[].id)")"
# The Graph scope ids are this tenant's (entra.tf: `email` differs from the commonly circulated id).
for p in openid:$GRAPH_SCOPE_OPENID profile:$GRAPH_SCOPE_PROFILE email:$GRAPH_SCOPE_EMAIL offline_access:$GRAPH_SCOPE_OFFLINE; do
  expect "Graph scope id for ${p%%:*}" "${p#*:}" "$(az ad sp show --id "$GRAPH_APPID" --query "oauth2PermissionScopes[?value=='${p%%:*}'].id | [0]" -o tsv)"
done
expect "app owner includes you" 1 "$(az ad app owner list --id "$APP_ID" --query "length([?id=='$ME'])" -o tsv)"
SP_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)
expect "SP appRoleAssignmentRequired (Gate 1)" true "$(az ad sp show --id "$APP_ID" --query appRoleAssignmentRequired -o tsv)"
expect "Gate 1: $GATE1_GROUP_NAME assigned directly" 1 \
  "$(az rest --method get --url "$G/servicePrincipals/$SP_ID/appRoleAssignedTo?\$top=999" --query "length(value[?principalId=='$GATE1_GROUP_ID' && appRoleId=='$DEFAULT_APP_ROLE'])" -o tsv)"
expect "Gate 1: nothing else assigned" 1 \
  "$(az rest --method get --url "$G/servicePrincipals/$SP_ID/appRoleAssignedTo?\$top=999" --query "length(value)" -o tsv)"
for g in "$GROUP_READERS" "$GROUP_EDITORS" "$GROUP_ADMINS"; do
  expect "$g exists once" 1 "$(az ad group list --display-name "$g" --query "length([?displayName=='$g'])" -o tsv)"
  expect "$g security/mail/role-assignable" "true false false" \
    "$(az ad group show --group "$g" --query "join(' ', [to_string(securityEnabled), to_string(mailEnabled), to_string(isAssignableToRole || \`false\`)])" -o tsv)"
  expect "$g owner includes you" 1 "$(az ad group owner list --group "$g" --query "length([?id=='$ME'])" -o tsv)"
done
echo "Entra app client id (OAuth__SharedClientId; Terraform output entra_app_client_id): $APP_ID"
echo "Phase 6 verified"
)
```

---

## Phase 7 — Secrets

Out-of-band in the Terraform too (`entra.tf`, `keyvault.tf`): neither value may live in any state or
file outside the vault. Two secrets go into `zendesk-prod-kv-uksouth`, each with a **180-day expiry**
so the scanner warns 30 days ahead:

| Vault secret | Source | Read by |
|---|---|---|
| `entra-mcp-client-secret` | a new Entra client secret on `Zendesk MCP` | nobody at runtime — it is the **record** of what is copied onto both apps as `entra-oauth-client-secret` (Phase 9). Rotating the vault copy alone changes nothing the app sends (vitally-mcp#138) |
| `zendesk-oauth-client-secret` | regenerated on the Zendesk OAuth client | the app, from the vault (`Zendesk__OAuthClientSecretRef`, cached 5 minutes) |

> ⚠️ **`printf %s`, never a `<<<` here-string.** A here-string appends a newline, `--file` stores it,
> and the app sends the secret as stored, so every connect, refresh and revoke fails with
> `invalid_client`. The block checks the stored length against the captured length.
>
> **The vault's data plane is private-endpoint only**, and Cloud Shell is outside the VNet, so the
> block opens a short *window*: a time-boxed **Key Vault Secrets Officer** for you, `defaultAction:
> Deny` **first**, then an IP rule for the Cloud Shell egress address, then public access on. An IP
> rule alone does not open it — `publicNetworkAccess: Disabled` short-circuits the ACL. A `trap`
> closes the window however the block ends, and restores the default action it found. If the vault
> never becomes reachable (the block gives up after 5 minutes), the vault may see Cloud Shell's
> traffic from a different address than `api.ipify.org` reports; run this phase from a workstation
> instead.
>
> ⚠️ **Not on the corporate VPN.** Its DNS answers `privatelink.vaultcore.azure.net` from a private
> zone that has no record for this vault, so the vault's name returns NXDOMAIN and the window never
> becomes reachable (`Failed to resolve 'zendesk-prod-kv-uksouth.vault.azure.net'`). WSL resolves
> through Windows, so a VPN connected in Windows affects WSL too. Check before running 7.2:
> `nslookup zendesk-prod-kv-uksouth.vault.azure.net` must reach public addresses. Found during the
> first build (2026-10-28, #3); off the VPN it worked first time.

### 7.1 Regenerate the Zendesk client secret and capture it

In Admin Centre → APIs → OAuth clients → `fiscal_zendesk_mcp`, **regenerate the secret** (the
current one was handled by hand during validation; it is shown once). Then, on its own line:

```bash
source ~/zendesk-estate.env; capture_secret zendesk-oauth-client-secret
```

### 7.2 Create the Entra client secret and store both

Re-runnable: the Entra credential is created only if `$SECRET_DIR/entra-oauth-client-secret` does
not already exist. If Cloud Shell restarted after a credential was created but before this block
finished, a re-run creates a **second** credential — delete the orphan afterwards
(`az ad app credential list --id <app objectId>`, then `az ad app credential delete … --key-id`).

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
[ -s "$SECRET_DIR/zendesk-oauth-client-secret" ] || { fail "run 7.1 (capture_secret) first"; exit 1; }
APP_OBJ=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].id" -o tsv)
nonempty "app object id" "$APP_OBJ"
END=$(date -u -d "+$SECRET_DAYS days" +%Y-%m-%dT%H:%M:%SZ)
umask 077
if [ ! -s "$SECRET_DIR/entra-oauth-client-secret" ]; then
  # addPassword APPENDS (az ad app credential reset without --append would REPLACE every credential).
  s=$(az rest --method post --url "https://graph.microsoft.com/v1.0/applications/$APP_OBJ/addPassword" \
        --body "{\"passwordCredential\":{\"displayName\":\"zendesk-mcp $(date -u +%F)\",\"endDateTime\":\"$END\"}}" \
        --query secretText -o tsv)
  [ -n "$s" ] || { fail "addPassword returned nothing"; exit 1; }
  printf %s "$s" > "$SECRET_DIR/entra-oauth-client-secret"; unset s
fi

KVW_ME=$(az ad signed-in-user show --query id -o tsv)
KVW_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
KVW_PREV=$(az keyvault show -g "$RG" -n "$KV" --query "properties.networkAcls.defaultAction" -o tsv)
KVW_IP=""
close_window() {
  set +e
  az keyvault update -g "$RG" -n "$KV" --public-network-access Disabled -o none || echo "!!! could not disable public access on $KV — do it by hand" >&2
  [ -n "$KVW_IP" ] && { az keyvault network-rule remove -g "$RG" -n "$KV" --ip-address "$KVW_IP/32" -o none || echo "!!! could not remove IP rule $KVW_IP" >&2; }
  az keyvault update -g "$RG" -n "$KV" --default-action "${KVW_PREV:-Allow}" -o none || echo "!!! could not restore default action ${KVW_PREV:-Allow}" >&2
  az role assignment delete --assignee "$KVW_ME" --role "Key Vault Secrets Officer" --scope "$KVW_ID" -o none || echo "!!! could not remove your Secrets Officer role" >&2
  echo "window closed"
}
trap close_window EXIT
[ "$(az role assignment list --scope "$KVW_ID" --assignee-object-id "$KVW_ME" --query "length([?roleDefinitionName=='Key Vault Secrets Officer'])" -o tsv)" = 1 ] \
  || az role assignment create --assignee-object-id "$KVW_ME" --assignee-principal-type User \
       --role "Key Vault Secrets Officer" --scope "$KVW_ID" -o none
KVW_IP=$(curl -4 -fsS https://api.ipify.org)          # resolved here, every time — never reused
case "$KVW_IP" in *.*.*.*) ;; *) fail "egress address '$KVW_IP' is not IPv4"; exit 1 ;; esac
az keyvault update -g "$RG" -n "$KV" --default-action Deny -o none          # BEFORE public access
az keyvault network-rule add -g "$RG" -n "$KV" --ip-address "$KVW_IP/32" -o none
az keyvault update -g "$RG" -n "$KV" --public-network-access Enabled -o none
ok=""; last=""
for i in $(seq 1 20); do
  last=$(az keyvault secret list --vault-name "$KV" -o none 2>&1) && { ok=1; break; }
  echo "  vault not reachable yet from $KVW_IP (attempt $i)"; sleep 15
done
# The last error tells a DNS failure, a network refusal and a role still propagating apart.
[ -n "$ok" ] || { fail "vault unreachable after 5 minutes — nothing stored. Last error: ${last:0:400}"; exit 1; }

for name in entra-mcp-client-secret:entra-oauth-client-secret zendesk-oauth-client-secret:zendesk-oauth-client-secret; do
  vault_name=${name%%:*}; file="$SECRET_DIR/${name#*:}"
  az keyvault secret set --vault-name "$KV" --name "$vault_name" --file "$file" --expires "$END" -o none
  expect "$vault_name stored length" "$(wc -c < "$file" | tr -d ' ')" \
    "$(az keyvault secret show --vault-name "$KV" --name "$vault_name" --query "length(value)" -o tsv)"
done
rm -f "$SECRET_DIR/zendesk-oauth-client-secret"      # the app reads it from the vault; no copy needed
)
```

`$SECRET_DIR/entra-oauth-client-secret` is kept until Phase 9 has copied it onto both apps, then
deleted. **If it is lost before then (Cloud Shell restarted), do not re-run 7.2** — its guard would
stop you, and getting past it would create a second Entra credential. Read the stored copy back from
the vault instead, through the same window, with this block on its own:

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
umask 077
KVW_ME=$(az ad signed-in-user show --query id -o tsv)
KVW_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
KVW_PREV=$(az keyvault show -g "$RG" -n "$KV" --query "properties.networkAcls.defaultAction" -o tsv)
KVW_IP=""
close_window() {
  set +e
  az keyvault update -g "$RG" -n "$KV" --public-network-access Disabled -o none || echo "!!! could not disable public access on $KV — do it by hand" >&2
  [ -n "$KVW_IP" ] && { az keyvault network-rule remove -g "$RG" -n "$KV" --ip-address "$KVW_IP/32" -o none || echo "!!! could not remove IP rule $KVW_IP" >&2; }
  az keyvault update -g "$RG" -n "$KV" --default-action "${KVW_PREV:-Allow}" -o none || echo "!!! could not restore default action ${KVW_PREV:-Allow}" >&2
  az role assignment delete --assignee "$KVW_ME" --role "Key Vault Secrets Officer" --scope "$KVW_ID" -o none || echo "!!! could not remove your Secrets Officer role" >&2
  echo "window closed"
}
trap close_window EXIT
[ "$(az role assignment list --scope "$KVW_ID" --assignee-object-id "$KVW_ME" --query "length([?roleDefinitionName=='Key Vault Secrets Officer'])" -o tsv)" = 1 ] \
  || az role assignment create --assignee-object-id "$KVW_ME" --assignee-principal-type User \
       --role "Key Vault Secrets Officer" --scope "$KVW_ID" -o none
KVW_IP=$(curl -4 -fsS https://api.ipify.org)          # resolved here, every time — never reused
case "$KVW_IP" in *.*.*.*) ;; *) fail "egress address '$KVW_IP' is not IPv4"; exit 1 ;; esac
az keyvault update -g "$RG" -n "$KV" --default-action Deny -o none          # BEFORE public access
az keyvault network-rule add -g "$RG" -n "$KV" --ip-address "$KVW_IP/32" -o none
az keyvault update -g "$RG" -n "$KV" --public-network-access Enabled -o none
ok=""; last=""
for i in $(seq 1 20); do
  last=$(az keyvault secret list --vault-name "$KV" -o none 2>&1) && { ok=1; break; }
  echo "  vault not reachable yet from $KVW_IP (attempt $i)"; sleep 15
done
# The last error tells a DNS failure, a network refusal and a role still propagating apart.
[ -n "$ok" ] || { fail "vault unreachable after 5 minutes — nothing stored. Last error: ${last:0:400}"; exit 1; }
az keyvault secret show --vault-name "$KV" --name entra-mcp-client-secret --query value -o tsv \
  | tr -d '\n' > "$SECRET_DIR/entra-oauth-client-secret"
[ -s "$SECRET_DIR/entra-oauth-client-secret" ] && echo "OK   recovered entra-oauth-client-secret" || { fail "nothing read back"; exit 1; }
)
```

**Verify** — through the **management plane**, so the window stays shut.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
APP_ID=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)
n=$(az ad app credential list --id "$APP_ID" --query "length([?starts_with(displayName || '', 'zendesk-mcp ')])" -o tsv)
[ "${n:-0}" -ge 1 ] && echo "OK   $n Entra client secret(s) named zendesk-mcp …" || fail "no Entra client secret"
[ "${n:-0}" -le 1 ] || echo "NOTE more than one credential — delete the orphan (see 7.2)"
KV_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
secrets() { az rest --method get --url "https://management.azure.com$KV_ID/secrets?api-version=$KV_API" --query "$1" -o tsv; }
soon=$(date -u -d "+$((SECRET_DAYS - 2)) days" +%s)
for s in entra-mcp-client-secret zendesk-oauth-client-secret; do
  exp=$(secrets "value[?name=='$s'] | [0].properties.attributes.exp")
  nonempty "$s expiry (epoch)" "$exp"
  [ "$exp" -gt "$soon" ] && echo "OK   $s expires $(date -u -d "@$exp" +%F)" || fail "$s expiry $(date -u -d "@$exp" +%F) is not ~$SECRET_DAYS days out"
done
# The window is shut.
kv() { az keyvault show -g "$RG" -n "$KV" --query "$1" -o tsv; }
expect "KV public network access" Disabled "$(kv properties.publicNetworkAccess)"
expect "KV IP rules" 0 "$(kv "length(properties.networkAcls.ipRules || \`[]\`)")"
expect "your temporary Secrets Officer removed" 0 \
  "$(az role assignment list --scope "$KV_ID" --assignee-object-id "$(az ad signed-in-user show --query id -o tsv)" --query "length([?roleDefinitionName=='Key Vault Secrets Officer'])" -o tsv)"
[ -s "$SECRET_DIR/entra-oauth-client-secret" ] && echo "OK   Entra secret held for Phase 9" || fail "Entra secret file missing — read it back (above)"
echo "Phase 7 verified"
)
```

---

## Phase 8 — The first image

`deploy.yml` rolls an *existing* app, so the image both apps are created with is imported by hand.
Cloud Shell has no Docker, so **build on a workstation** with Docker Desktop (PowerShell shown), from
a clean `main`:

```powershell
cd YOUR_CLONE_OF_ZENDESK_MCP                     # replace; `<…>` placeholders do not parse in PowerShell
git checkout main; git pull --ff-only; git status --porcelain   # must print nothing
$tag = "sha-$(git rev-parse --short HEAD)"
docker login ghcr.io -u YOUR_GITHUB_USER        # replace; password: a PAT with write:packages
docker build -t "ghcr.io/fiscaltec/zendesk-mcp:$tag" .
docker push "ghcr.io/fiscaltec/zendesk-mcp:$tag"
$tag                                            # note it for the next step
```

If the push created the GHCR package, grant the repository access to it (package settings → *Manage
Actions access* → `fiscaltec/zendesk-mcp`, role **Write**) so `deploy.yml` can push later.

Back in Cloud Shell: record the tag (both apps are created on the same image), name the GitHub user
that owns the import PAT, and capture a **read-only** PAT (`read:packages`) — each line on its own:

```bash
source ~/zendesk-estate.env; read -r -p 'Image tag (sha-…): ' t && sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$t/; s/^STAGING_IMAGE_TAG=.*/STAGING_IMAGE_TAG=$t/" ~/zendesk-estate.env && unset t
```

```bash
read -r -p 'GitHub user owning the read:packages PAT: ' GH_USER && export GH_USER
```

```bash
source ~/zendesk-estate.env; capture_secret ghcr-read-pat
```

A classic PAT is required (fine-grained tokens cannot read GHCR); if `fiscaltec` enforces SAML SSO,
authorise it for the organisation or the import fails with 403. **Alternatively, use the `gh` CLI's
own token** in place of the two lines above — no copy-paste, but its scope is wider (`repo`,
`read:org`, `gist` plus `read:packages`) and it does not expire, so revoke it afterwards (GitHub
Settings → Applications → Authorized OAuth Apps → GitHub CLI → Revoke; `gh auth logout` only deletes
the local copy). Used for the first build (2026-10-28, #3). After
`gh auth login --hostname github.com --git-protocol https --web --scopes read:packages`:

```bash
export GH_USER=$(gh api user --jq .login) && source ~/zendesk-estate.env && gh auth token | capture_secret ghcr-read-pat
```

Import server-side into the shared registry (public access is off; `az acr import` works regardless).
**Name the `zendesk-mcp` repository explicitly** — the registry also holds `vitally-mcp`. No `--force`:
this must never overwrite a tag. Not re-runnable as such: if it fails with *already exists*, an
earlier run imported it — carry on.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
nonempty "IMAGE_TAG" "$IMAGE_TAG"
printf '%s' "$IMAGE_TAG" | grep -Eq '^[A-Za-z0-9._-]+$' || { fail "invalid tag '$IMAGE_TAG'"; exit 1; }
nonempty "GH_USER" "${GH_USER:-}"
trap 'rm -f "$SECRET_DIR/ghcr-read-pat"' EXIT     # removed on failure too
# az acr import takes the source password only as an argument, so for the seconds this runs the PAT
# is visible in your own Cloud Shell's process list. Use a short-lived, read:packages-only PAT.
az acr import --name "$ACR" --resource-group "$ACR_RG" \
  --source "$GHCR_IMAGE:$IMAGE_TAG" --image "$IMAGE_REPO:$IMAGE_TAG" \
  --username "$GH_USER" --password "$(cat "$SECRET_DIR/ghcr-read-pat")" -o none
rm -f "$SECRET_DIR/ghcr-read-pat"
echo "imported $ACR.azurecr.io/$IMAGE_REPO:$IMAGE_TAG"
)
```

**Verify.** There is no honest read-back from Cloud Shell: listing tags is a *data-plane* call, and
the registry's data plane is private. The import's exit status above is the first check, and the
**proof** is Phase 9 — a production revision that reaches `Running`/`Healthy` has pulled exactly this
tag over the private endpoint with the identity's AcrPull. Do not mark Phase 8 done until Phase 9's
verify passes.

---

## Phase 9 — Container Apps environment, both apps, the scanner Job

`containerapps.tf`, `containerapps-staging.tf`, `diagnostics.tf`, and the two app-scoped
`Contributor` assignments from `identity.tf`.

> ⚠️ **`logs_destination = azure-monitor` and the diagnostic setting are one mechanism.** The default
> `log-analytics` destination is a shared-key shipper the workspace refuses (local auth off), and it
> is unsupported over Private Link — it delivered nothing for the Vitally estate's whole life
> (vitally-mcp#142). With `azure-monitor`, the diagnostic setting (9.2) is what delivers; without it
> logs go nowhere, silently. Change the two together or neither.
>
> ⚠️ **`max_replicas = 1` on both apps, and do not raise it.** Connect links, Zendesk OAuth attempt
> state, the Entra proxy's state and upload slots live in an in-process cache; scaling out needs
> Redis first.
>
> ⚠️ **Staging keeps `Authorization__ReadOnly=true`.** It runs unreleased code against the one live
> Zendesk account, as the signed-in person.
>
> ⚠️ **Every app in this environment must set `ApplicationInsights__ConnectionString`.** Console logs
> are exported for the whole environment, and without that variable an app's unsuppressed audit
> records (caller object ids, tool arguments, ticket ids) land in the console table.
>
> The Terraform defines no probes, so none are set here; Container Apps applies its defaults.

### 9.1 Environment

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
SNET_APP_ID=$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_APP" --query id -o tsv)
az containerapp env show -g "$RG" -n "$CAE" -o none 2>/dev/null \
  || az containerapp env create -g "$RG" -n "$CAE" -l "$LOC" \
       --infrastructure-subnet-resource-id "$SNET_APP_ID" --internal-only false \
       --enable-workload-profiles true --logs-destination azure-monitor -o none
)
```

### 9.2 Diagnostic setting — system and console logs to the workspace

`ContainerAppHTTPLogs` is deliberately absent: it carries request URLs, including the one-time
`/zendesk/connect` and `/zendesk/callback` query strings. `--export-to-resource-specific` is
deliberately **not** passed (Terraform leaves `log_analytics_destination_type` unset; the API stores
null for this resource type whatever is sent, and rows land in the resource-specific tables anyway).

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
CAE_ID=$(az containerapp env show -g "$RG" -n "$CAE" --query id -o tsv)
az monitor diagnostic-settings show -n cae-system-logs --resource "$CAE_ID" -o none 2>/dev/null \
  || az monitor diagnostic-settings create -n cae-system-logs --resource "$CAE_ID" \
       --workspace "$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)" \
       --logs '[{"category":"ContainerAppSystemLogs","enabled":true},{"category":"ContainerAppConsoleLogs","enabled":true}]' -o none
)
```

### 9.3 Production and staging apps

The YAML is generated from the env file and live lookups, so the environment-variable list is the
Terraform's exactly: the shared set (`app_env_shared`) plus each target's own. All values are
single-quoted strings (`"true"`, not YAML `true`). The file carries the Entra secret, so it is written
to `$SECRET_DIR` and deleted with it. **Not re-runnable as an update:** an existing app is skipped;
change a live app with `az containerapp update` / `secret set`, and the Terraform to match.

```bash
cat > ~/zendesk-estate-apps.sh <<'APPS'
# Generates the Container App definitions (containerapps.tf, containerapps-staging.tf). Sourced by
# Phase 9 and its verify block. Prints NAME<TAB>VALUE for every plain (non-secret) env var.
app_env() {  # app_env prod|staging
  local kv_uri mi_cid app_id rd ed ad conn v
  kv_uri=$(az keyvault show -g "$RG" -n "$KV" --query properties.vaultUri -o tsv)
  mi_cid=$(az identity show -g "$RG" -n "$MI" --query clientId -o tsv)
  app_id=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)
  rd=$(az ad group show --group "$GROUP_READERS" --query id -o tsv)
  ed=$(az ad group show --group "$GROUP_EDITORS" --query id -o tsv)
  ad=$(az ad group show --group "$GROUP_ADMINS" --query id -o tsv)
  conn=$(az monitor app-insights component show --app "$APPI" -g "$RG" --query connectionString -o tsv)
  for v in "$kv_uri" "$mi_cid" "$app_id" "$rd" "$ed" "$ad" "$conn"; do
    [ -n "$v" ] || { echo "FAIL app_env: a lookup came back empty" >&2; return 1; }
  done
  printf '%s\t%s\n' \
    Zendesk__Subdomain "$ZD_SUBDOMAIN" \
    Zendesk__KeyVaultUri "$kv_uri" \
    Zendesk__OAuthClientId "$ZD_CLIENT_ID" \
    AZURE_CLIENT_ID "$mi_cid" \
    OAuth__Authority "$OAUTH_AUTHORITY" \
    OAuth__Audience "$OAUTH_AUDIENCE" \
    OAuth__UpstreamResourceScope "$OAUTH_SCOPE" \
    OAuth__NoAuth false \
    OAuth__SharedClientId "$app_id" \
    OAuth__AllowedClientRedirectUris__0 "$ALLOWED_REDIRECT" \
    Authorization__LiveGroupCheck true \
    Authorization__ReaderGroupId "$rd" \
    Authorization__EditorGroupId "$ed" \
    Authorization__AdminGroupId "$ad" \
    ApplicationInsights__ConnectionString "$conn"
  case "$1" in
    prod) printf '%s\t%s\n' \
      Zendesk__TokenSecretPrefix zendesk-user- \
      OAuth__Resource "$OAUTH_RESOURCE" \
      OAuth__PublicBaseUrl "$PROD_ORIGIN" \
      Zendesk__Features__AllowPublicReplies "$PROD_ALLOW_PUBLIC_REPLIES" \
      Zendesk__Features__AllowArticlePublishing "$PROD_ALLOW_ARTICLE_PUBLISHING" \
      Zendesk__Features__AllowHelpCenterStructureChanges "$PROD_ALLOW_HC_STRUCTURE" \
      Zendesk__Features__AllowMacroChanges "$PROD_ALLOW_MACRO_CHANGES" \
      Zendesk__Features__AllowUserAndOrganizationChanges "$PROD_ALLOW_USER_ORG_CHANGES" ;;
    staging) printf '%s\t%s\n' \
      Zendesk__TokenSecretPrefix zendesk-staging-user- \
      OAuth__Resource "$STG_OAUTH_RESOURCE" \
      OAuth__PublicBaseUrl "$STG_ORIGIN" \
      Authorization__ReadOnly "$STG_READ_ONLY" \
      Zendesk__Features__AllowPublicReplies "$STG_ALLOW_PUBLIC_REPLIES" \
      Zendesk__Features__AllowArticlePublishing "$STG_ALLOW_ARTICLE_PUBLISHING" \
      Zendesk__Features__AllowHelpCenterStructureChanges "$STG_ALLOW_HC_STRUCTURE" \
      Zendesk__Features__AllowMacroChanges "$STG_ALLOW_MACRO_CHANGES" \
      Zendesk__Features__AllowUserAndOrganizationChanges "$STG_ALLOW_USER_ORG_CHANGES" ;;
    *) echo "FAIL app_env: target must be prod or staging" >&2; return 1 ;;
  esac
}
app_yaml() {  # app_yaml prod|staging <image tag> <min replicas>
  local mi_id cae_id login secret envs
  mi_id=$(az identity show -g "$RG" -n "$MI" --query id -o tsv)
  cae_id=$(az containerapp env show -g "$RG" -n "$CAE" --query id -o tsv)
  login=$(az acr show -g "$ACR_RG" -n "$ACR" --query loginServer -o tsv)
  secret=$(cat "$SECRET_DIR/entra-oauth-client-secret")
  envs=$(app_env "$1") || return 1
  [ -n "$mi_id" ] && [ -n "$cae_id" ] && [ -n "$login" ] && [ -n "$secret" ] && [ -n "$2" ] \
    || { echo "FAIL app_yaml: a lookup came back empty" >&2; return 1; }
  cat <<EOF
location: $LOC
identity:
  type: UserAssigned
  userAssignedIdentities:
    '$mi_id': {}
properties:
  environmentId: '$cae_id'
  workloadProfileName: Consumption
  configuration:
    activeRevisionsMode: Single
    ingress:
      external: true
      targetPort: 8080
      transport: auto
      allowInsecure: false   # omitted, az sends null, which the 2025-07-01 API rejects (#3)
      traffic:
      - latestRevision: true
        weight: 100
    registries:
    - server: '$login'
      identity: '$mi_id'
    secrets:
    - name: entra-oauth-client-secret
      value: '$secret'
  template:
    containers:
    - name: zendesk-mcp
      image: '$login/$IMAGE_REPO:$2'
      resources:
        cpu: 0.5
        memory: 1Gi
      env:
$(printf '%s\n' "$envs" | while IFS=$'\t' read -r n v; do printf "      - name: %s\n        value: '%s'\n" "$n" "$v"; done)
      - name: OAuth__SharedClientSecret
        secretRef: entra-oauth-client-secret
    scale:
      minReplicas: $3
      maxReplicas: 1
EOF
}
APPS
echo "wrote ~/zendesk-estate-apps.sh"
```

```bash
( set -euo pipefail; source ~/zendesk-estate.env; source ~/zendesk-estate-apps.sh; assert_context
nonempty "IMAGE_TAG" "$IMAGE_TAG"; nonempty "STAGING_IMAGE_TAG" "$STAGING_IMAGE_TAG"
[ -s "$SECRET_DIR/entra-oauth-client-secret" ] || { fail "Entra secret file missing — see Phase 7"; exit 1; }
umask 077
trap 'rm -f "$SECRET_DIR/prod.yaml" "$SECRET_DIR/staging.yaml"' EXIT   # both carry the Entra secret
# Production: one replica always warm, and never more than one.
if ! az containerapp show -g "$RG" -n "$APP_PROD" -o none 2>/dev/null; then
  app_yaml prod "$IMAGE_TAG" 1 > "$SECRET_DIR/prod.yaml"
  az containerapp create -g "$RG" -n "$APP_PROD" --yaml "$SECRET_DIR/prod.yaml" -o none
fi
# Staging: scale to zero, never more than one.
if ! az containerapp show -g "$RG" -n "$APP_STG" -o none 2>/dev/null; then
  app_yaml staging "$STAGING_IMAGE_TAG" 0 > "$SECRET_DIR/staging.yaml"
  az containerapp create -g "$RG" -n "$APP_STG" --yaml "$SECRET_DIR/staging.yaml" -o none
fi
rm -f "$SECRET_DIR/prod.yaml" "$SECRET_DIR/staging.yaml"

# Deploy: roll each app — scoped to the app, so a deploy to one target cannot roll the other.
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)
for app in "$APP_PROD" "$APP_STG"; do
  scope=$(az containerapp show -g "$RG" -n "$app" --query id -o tsv)
  [ "$(az role assignment list --scope "$scope" --assignee-object-id "$MI_PID" --query "length([?roleDefinitionName=='Contributor'])" -o tsv)" = 1 ] \
    || az role assignment create --assignee-object-id "$MI_PID" --assignee-principal-type ServicePrincipal \
         --role Contributor --scope "$scope" -o none
done
)
```

### 9.4 Secret-expiry scanner Job

The Terraform passes `scan/run.py` to a stock `python:3-slim` image as base64 inside the command
(`locals.scanner_command`); this builds the identical string from the repo clone. **It needs the Teams
webhook, which is still an open decision** (`infra/terraform/README.md`, "Still open" 3). Until it
exists, skip 9.4 and its checks and come back to it; nothing else depends on the Job.

```bash
source ~/zendesk-estate.env; capture_secret teams-webhook
```

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
[ -s "$SECRET_DIR/teams-webhook" ] || { fail "capture the Teams webhook first"; exit 1; }
az containerapp job show -g "$RG" -n "$JOB" -o none 2>/dev/null && { echo "job exists — not recreated"; exit 0; }
SCAN_CMD="echo $(base64 -w0 "$REPO_DIR/infra/terraform/scan/run.py") | base64 -d > /tmp/run.py && python3 /tmp/run.py"
MI_ID=$(az identity show -g "$RG" -n "$MI" --query id -o tsv)
MI_CID=$(az identity show -g "$RG" -n "$MI" --query clientId -o tsv)
CAE_ID=$(az containerapp env show -g "$RG" -n "$CAE" --query id -o tsv)
umask 077
trap 'rm -f "$SECRET_DIR/job.yaml"' EXIT          # carries the webhook; removed on failure too
cat > "$SECRET_DIR/job.yaml" <<EOF
location: $LOC
identity:
  type: UserAssigned
  userAssignedIdentities:
    '$MI_ID': {}
properties:
  environmentId: '$CAE_ID'
  workloadProfileName: Consumption
  configuration:
    triggerType: Schedule
    replicaTimeout: 1800
    replicaRetryLimit: 1
    scheduleTriggerConfig:
      cronExpression: '$SCANNER_CRON'
      parallelism: 1
      replicaCompletionCount: 1
    secrets:
    - name: teams-webhook
      value: '$(cat "$SECRET_DIR/teams-webhook")'
  template:
    containers:
    - name: secretscan
      image: '$SCANNER_IMAGE'
      command:
      - /bin/sh
      args:
      - '-c'
      - '$SCAN_CMD'
      resources:
        cpu: 0.5
        memory: 1Gi
      env:
      - name: MI_CLIENT_ID
        value: '$MI_CID'
      - name: VAULT
        value: '$KV'
      - name: TEAMS_WEBHOOK
        secretRef: teams-webhook
EOF
az containerapp job create -g "$RG" -n "$JOB" --yaml "$SECRET_DIR/job.yaml" -o none
rm -f "$SECRET_DIR/job.yaml" "$SECRET_DIR/teams-webhook"
)
```

**Verify** (9.4 checks are skipped while the Job does not exist; re-run this block after creating it)

```bash
( set -euo pipefail; source ~/zendesk-estate.env; source ~/zendesk-estate-apps.sh; assert_context
CAE_ID=$(az containerapp env show -g "$RG" -n "$CAE" --query id -o tsv)
e() { az containerapp env show -g "$RG" -n "$CAE" --query "$1" -o tsv; }
expect "CAE logs destination" azure-monitor "$(e properties.appLogsConfiguration.destination)"
expect_ci "CAE subnet" "$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_APP" --query id -o tsv)" "$(e properties.vnetConfiguration.infrastructureSubnetId)"
expect "CAE internal only" false "$(e properties.vnetConfiguration.internal)"
expect "CAE Consumption profile" Consumption "$(e "properties.workloadProfiles[?name=='Consumption'].workloadProfileType | [0]")"
ds() { az monitor diagnostic-settings show -n cae-system-logs --resource "$CAE_ID" --query "$1" -o tsv; }
expect "CAE diag categories" "ContainerAppConsoleLogs,ContainerAppSystemLogs" "$(ds "join(',', sort(logs[?enabled].category))")"
expect_ci "CAE diag workspace" "$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)" "$(ds workspaceId)"
MI_ID=$(az identity show -g "$RG" -n "$MI" --query id -o tsv)
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)
LOGIN=$(az acr show -g "$ACR_RG" -n "$ACR" --query loginServer -o tsv)
SECRET_LEN=$( [ -s "$SECRET_DIR/entra-oauth-client-secret" ] && wc -c < "$SECRET_DIR/entra-oauth-client-secret" | tr -d ' ' || echo "")
for t in prod:$APP_PROD:1:$IMAGE_TAG staging:$APP_STG:0:$STAGING_IMAGE_TAG; do
  IFS=: read -r target app min tag <<< "$t"
  c() { az containerapp show -g "$RG" -n "$app" --query "$1" -o tsv; }
  expect_ci "$app environment" "$CAE_ID" "$(c properties.environmentId)"
  expect "$app workload profile" Consumption "$(c properties.workloadProfileName)"
  expect "$app revision mode" Single "$(c properties.configuration.activeRevisionsMode)"
  expect "$app identity type" UserAssigned "$(c identity.type)"
  expect_ci "$app identity" "$MI_ID" "$(c "keys(identity.userAssignedIdentities)[0]")"
  expect "$app registry server" "$LOGIN" "$(c "properties.configuration.registries[0].server")"
  expect_ci "$app registry identity" "$MI_ID" "$(c "properties.configuration.registries[0].identity")"
  expect "$app ingress external" true "$(c properties.configuration.ingress.external)"
  expect "$app ingress port" 8080 "$(c properties.configuration.ingress.targetPort)"
  expect_ci "$app ingress transport" auto "$(c properties.configuration.ingress.transport)"
  expect "$app ingress allowInsecure" false "$(c properties.configuration.ingress.allowInsecure)"
  expect "$app traffic" "true 100" "$(c "properties.configuration.ingress.traffic[0].join(' ', [to_string(latestRevision), to_string(weight)])")"
  expect "$app min replicas" "$min" "$(c properties.template.scale.minReplicas)"
  expect "$app max replicas" 1 "$(c properties.template.scale.maxReplicas)"
  expect "$app container" zendesk-mcp "$(c "properties.template.containers[0].name")"
  expect "$app image" "$LOGIN/$IMAGE_REPO:$tag" "$(c "properties.template.containers[0].image")"
  expect "$app cpu/memory" "0.5 1Gi" "$(c "properties.template.containers[0].resources.join(' ', [to_string(cpu), memory])")"
  expect "$app secrets" entra-oauth-client-secret "$(az containerapp secret list -g "$RG" -n "$app" --query "join(',', [].name)" -o tsv)"
  expect "$app secret-backed env" "OAuth__SharedClientSecret=entra-oauth-client-secret" \
    "$(c "join(',', properties.template.containers[0].env[?secretRef].join('=', [name, secretRef]))")"
  diff <(app_env "$target" | sort) <(c "properties.template.containers[0].env[?value].[name, value]" | sort) \
    && echo "OK   $app plain env matches the Terraform set exactly" || fail "$app env differs (< expected, > live)"
  if [ -n "$SECRET_LEN" ]; then
    expect "$app secret length (no trailing newline)" "$SECRET_LEN" \
      "$(az containerapp secret show -g "$RG" -n "$app" --secret-name entra-oauth-client-secret --query "length(value)" -o tsv)"
  fi
  expect "$app Contributor for deploys" 1 "$(az role assignment list --scope "$(c id)" --assignee-object-id "$MI_PID" --query "length([?roleDefinitionName=='Contributor'])" -o tsv)"
  expect "$app latest revision provisioned" Provisioned \
    "$(az containerapp revision show -g "$RG" -n "$app" --revision "$(c properties.latestRevisionName)" --query properties.provisioningState -o tsv)"
done
expect "staging read-only" "$STG_READ_ONLY" "$(az containerapp show -g "$RG" -n "$APP_STG" --query "properties.template.containers[0].env[?name=='Authorization__ReadOnly'].value | [0]" -o tsv)"
# The image pulled and the app serves (this is Phase 8's proof). Default FQDNs — no custom domain yet.
for app in "$APP_PROD" "$APP_STG"; do
  fqdn=$(az containerapp show -g "$RG" -n "$app" --query properties.configuration.ingress.fqdn -o tsv)
  nonempty "$app default FQDN (CNAME target)" "$fqdn"
  code=""; for i in $(seq 1 12); do code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "https://$fqdn/health" || true); [ "$code" = 200 ] && break; sleep 10; done
  expect "$app /health on default FQDN" 200 "$code"
done
expect "production revision health" Healthy \
  "$(az containerapp revision show -g "$RG" -n "$APP_PROD" --revision "$(az containerapp show -g "$RG" -n "$APP_PROD" --query properties.latestRevisionName -o tsv)" --query properties.healthState -o tsv)"
if az containerapp job show -g "$RG" -n "$JOB" -o none 2>/dev/null; then
  SCAN_CMD="echo $(base64 -w0 "$REPO_DIR/infra/terraform/scan/run.py") | base64 -d > /tmp/run.py && python3 /tmp/run.py"
  j() { az containerapp job show -g "$RG" -n "$JOB" --query "$1" -o tsv; }
  expect_ci "job environment" "$CAE_ID" "$(j properties.environmentId)"
  expect "job workload profile" Consumption "$(j properties.workloadProfileName)"
  expect_ci "job identity" "$MI_ID" "$(j "keys(identity.userAssignedIdentities)[0]")"
  expect "job trigger" Schedule "$(j properties.configuration.triggerType)"
  expect "job cron" "$SCANNER_CRON" "$(j properties.configuration.scheduleTriggerConfig.cronExpression)"
  expect "job parallelism/completions" "1 1" "$(j "properties.configuration.scheduleTriggerConfig.join(' ', [to_string(parallelism), to_string(replicaCompletionCount)])")"
  expect "job timeout/retries" "1800 1" "$(j "properties.configuration.join(' ', [to_string(replicaTimeout), to_string(replicaRetryLimit)])")"
  expect "job image" "$SCANNER_IMAGE" "$(j "properties.template.containers[0].image")"
  expect "job container" secretscan "$(j "properties.template.containers[0].name")"
  expect "job cpu/memory" "0.5 1Gi" "$(j "properties.template.containers[0].resources.join(' ', [to_string(cpu), memory])")"
  expect "job command" /bin/sh "$(j "join(',', properties.template.containers[0].command)")"
  expect "job args[0]" -c "$(j "properties.template.containers[0].args[0]")"
  expect "job args[1] = scanner_command (current scan/run.py)" "$SCAN_CMD" "$(j "properties.template.containers[0].args[1]")"
  expect "job env" "MI_CLIENT_ID=$(az identity show -g "$RG" -n "$MI" --query clientId -o tsv),VAULT=$KV" \
    "$(j "join(',', properties.template.containers[0].env[?value].join('=', [name, value]))")"
  expect "job secret env" "TEAMS_WEBHOOK=teams-webhook" "$(j "join(',', properties.template.containers[0].env[?secretRef].join('=', [name, secretRef]))")"
  expect "job secrets" teams-webhook "$(az containerapp job secret list -g "$RG" -n "$JOB" --query "join(',', [].name)" -o tsv)"
else
  echo "NOTE scanner Job not created yet (9.4 waits on the Teams webhook)"
fi
echo "Phase 9 verified"
)
```

When both apps verify, delete the last secret copy:
`rm -rf /tmp/zendesk-estate-secrets` (the vault's `entra-mcp-client-secret` is the record).

Optionally prove the Job end to end with `az containerapp job start -g "$RG" -n "$JOB"` and read its
console output (`[scan] token acquired` … `[scan] none within 30 days` or `posted to Teams`) with
`az containerapp job logs show -g "$RG" -n "$JOB" --container secretscan`.

---

## Phase 10 — Graph permission for the identity, and admin consent

Both out-of-band in the Terraform too, because they need a more privileged principal than routine
work. **Needs Privileged Role Administrator.** Until 10.1 is done, every tool call is denied — the
group resolver never fails open.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
G=https://graph.microsoft.com/v1.0
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)
GRAPH_SP=$(az ad sp show --id "$GRAPH_APPID" --query id -o tsv)
ROLE=$(az ad sp show --id "$GRAPH_APPID" --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
nonempty "GroupMember.Read.All role id" "$ROLE"
# 10.1 Gate 2: the identity reads sg-zendesk-* membership with its own application permission.
have=$(az rest --method get --url "$G/servicePrincipals/$MI_PID/appRoleAssignments" \
         --query "length(value[?appRoleId=='$ROLE' && resourceId=='$GRAPH_SP'])" -o tsv)
[ "$have" = 1 ] || az rest --method post --url "$G/servicePrincipals/$MI_PID/appRoleAssignments" \
  --body "{\"principalId\":\"$MI_PID\",\"resourceId\":\"$GRAPH_SP\",\"appRoleId\":\"$ROLE\"}" -o none
# 10.2 Tenant-wide admin consent for openid profile email offline_access mcp.access, so nobody sees
#      a consent screen. Idempotent.
az ad app permission admin-consent --id "$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)"
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
G=https://graph.microsoft.com/v1.0
MI_PID=$(az identity show -g "$RG" -n "$MI" --query principalId -o tsv)
GRAPH_SP=$(az ad sp show --id "$GRAPH_APPID" --query id -o tsv)
ROLE=$(az ad sp show --id "$GRAPH_APPID" --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
expect "identity holds GroupMember.Read.All" 1 \
  "$(az rest --method get --url "$G/servicePrincipals/$MI_PID/appRoleAssignments" --query "length(value[?appRoleId=='$ROLE' && resourceId=='$GRAPH_SP'])" -o tsv)"
APP_ID=$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[?displayName=='$ENTRA_APP_NAME'] | [0].appId" -o tsv)
SP_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)
grants=$(az rest --method get --url "$G/oauth2PermissionGrants?\$filter=clientId%20eq%20'$SP_ID'" -o json)
scopes_for() { printf '%s' "$grants" | python3 -c 'import json,sys; g=json.load(sys.stdin)["value"]; print(" ".join(sorted(set(" ".join(x["scope"] for x in g if x["consentType"]=="AllPrincipals" and x["resourceId"]==sys.argv[1]).split()))))' "$1"; }
expect "admin consent on Graph" "email offline_access openid profile" "$(scopes_for "$GRAPH_SP")"
expect "admin consent on own API" "mcp.access" "$(scopes_for "$SP_ID")"
echo "Phase 10 verified"
)
```

---

## Phase 11 — Custom domains with managed certificates

`fiscaltec.com` is on Cloudflare, so DNS is not built here. Managed certificates cannot be created by
the Terraform either, so this was always out-of-band.

**11.1 In Cloudflare**, for each host, two records — print the values:

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
VID=$(az containerapp env show -g "$RG" -n "$CAE" --query properties.customDomainConfiguration.customDomainVerificationId -o tsv)
nonempty "verification id (Terraform output custom_domain_verification_id)" "$VID"
for p in "$PROD_HOST:$APP_PROD" "$STG_HOST:$APP_STG"; do
  fqdn=$(az containerapp show -g "$RG" -n "${p#*:}" --query properties.configuration.ingress.fqdn -o tsv)
  echo "CNAME  ${p%%:*}  ->  $fqdn      (DNS only — NOT proxied)"
  echo "TXT    asuid.${p%%:*}  =  $VID"
done
)
```

⚠️ **The CNAME must be DNS-only (grey cloud).** Proxying it breaks managed-certificate issuance.

**11.2 Bind** once both records resolve. `hostname add` is guarded; `bind` is re-runnable.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
VID=$(az containerapp env show -g "$RG" -n "$CAE" --query properties.customDomainConfiguration.customDomainVerificationId -o tsv)
for p in "$PROD_HOST:$APP_PROD" "$STG_HOST:$APP_STG"; do
  host=${p%%:*}; app=${p#*:}
  fqdn=$(az containerapp show -g "$RG" -n "$app" --query properties.configuration.ingress.fqdn -o tsv)
  expect "$host CNAME" "$fqdn." "$(dig +short CNAME "$host" @1.1.1.1 | head -1)"
  expect "asuid.$host TXT" "$VID" "$(dig +short TXT "asuid.$host" @1.1.1.1 | tr -d '"' | head -1)"
  [ "$(az containerapp hostname list -g "$RG" -n "$app" --query "length([?name=='$host'])" -o tsv)" = 1 ] \
    || az containerapp hostname add -g "$RG" -n "$app" --hostname "$host" -o none
  az containerapp hostname bind -g "$RG" -n "$app" --hostname "$host" --environment "$CAE" --validation-method CNAME -o none
done
)
```

**Verify** (certificate issuance can take several minutes; re-run until it passes)

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
for p in "$PROD_HOST:$APP_PROD" "$STG_HOST:$APP_STG"; do
  host=${p%%:*}; app=${p#*:}; origin=https://$host
  expect "$host binding" SniEnabled "$(az containerapp hostname list -g "$RG" -n "$app" --query "[?name=='$host'].bindingType | [0]" -o tsv)"
  expect "$host managed certificate" Succeeded \
    "$(az containerapp env certificate list -g "$RG" -n "$CAE" --managed-certificates-only --query "[?properties.subjectName=='$host'].properties.provisioningState | [0]" -o tsv)"
  code=""; for i in $(seq 1 12); do code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "$origin/health" || true); [ "$code" = 200 ] && break; sleep 10; done
  expect "$origin/health" 200 "$code"
done
echo "Phase 11 verified"
)
```

---

## Phase 12 — Tier membership (Gate 2)

As decided (README "Decisions"): the IT & Security Department reads, and Dan Searle holds every tier
(cumulative, so `sg-zendesk-admins` alone would do; all three keeps the record explicit). Membership
is not in the Terraform by design — admins grant and revoke tiers without a build (`ACCESS.md`).
Adding a department later means **both** a direct Gate 1 assignment (Phase 6.7) **and** nesting it
into one tier group here.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
add() {  # add <group> <member object id>
  [ "$(az ad group member list --group "$1" --query "length([?id=='$2'])" -o tsv)" = 1 ] \
    || az ad group member add --group "$1" --member-id "$2" -o none
}
add "$GROUP_READERS" "$GATE1_GROUP_ID"
ME_TIER=$(az ad user show --id "$TIER_INDIVIDUAL" --query id -o tsv)
nonempty "$TIER_INDIVIDUAL object id" "$ME_TIER"
for g in "$GROUP_READERS" "$GROUP_EDITORS" "$GROUP_ADMINS"; do add "$g" "$ME_TIER"; done
)
```

**Verify** — direct members, exactly what was decided and nothing else.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
ME_TIER=$(az ad user show --id "$TIER_INDIVIDUAL" --query id -o tsv)
m() { az ad group member list --group "$1" --query "join(',', sort([].id))" -o tsv; }
expect "$GROUP_READERS members" "$(printf '%s\n' "$GATE1_GROUP_ID" "$ME_TIER" | sort | paste -sd,)" "$(m "$GROUP_READERS")"
expect "$GROUP_EDITORS members" "$ME_TIER" "$(m "$GROUP_EDITORS")"
expect "$GROUP_ADMINS members" "$ME_TIER" "$(m "$GROUP_ADMINS")"
echo "Phase 12 verified"
)
```

---

## Phase 13 — GitHub

Repository variables and secrets for `deploy.yml`, `release.yml` and `staging-gate.yml`, and the two
environments whose names the federated credentials (Phase 4) match. `gh` in Cloud Shell, logged in as
a repository admin (`gh auth login`). All of it is re-runnable.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
gh variable set ACR_NAME       --repo "$GH_REPO" --body "$ACR"
gh variable set RESOURCE_GROUP --repo "$GH_REPO" --body "$RG"
gh variable set IMAGE_NAME     --repo "$GH_REPO" --body "$IMAGE_REPO"
az identity show -g "$RG" -n "$MI" --query clientId -o tsv | tr -d '\n' | gh secret set AZURE_CLIENT_ID --repo "$GH_REPO"
printf %s "$TENANT" | gh secret set AZURE_TENANT_ID       --repo "$GH_REPO"
printf %s "$SUB"    | gh secret set AZURE_SUBSCRIPTION_ID --repo "$GH_REPO"
for t in "production:$APP_PROD:$PROD_ORIGIN" "staging:$APP_STG:$STG_ORIGIN"; do
  env=${t%%:*}; rest=${t#*:}; app=${rest%%:*}; origin=${rest#*:}
  # Create the environment only if it is missing: a PUT on an existing one can reset its protection
  # rules (required reviewers, wait timer).
  gh api "repos/$GH_REPO/environments/$env" --silent 2>/dev/null || gh api -X PUT "repos/$GH_REPO/environments/$env" --silent
  gh variable set CONTAINER_APP --repo "$GH_REPO" --env "$env" --body "$app"
  gh variable set PUBLIC_ORIGIN --repo "$GH_REPO" --env "$env" --body "$origin"   # origin only: no path, no slash
done
)
```

**Verify**

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
expect "ACR_NAME"       "$ACR"        "$(gh variable get ACR_NAME --repo "$GH_REPO")"
expect "RESOURCE_GROUP" "$RG"         "$(gh variable get RESOURCE_GROUP --repo "$GH_REPO")"
expect "IMAGE_NAME"     "$IMAGE_REPO" "$(gh variable get IMAGE_NAME --repo "$GH_REPO")"
expect "repo secrets present" "AZURE_CLIENT_ID,AZURE_SUBSCRIPTION_ID,AZURE_TENANT_ID" \
  "$(gh secret list --repo "$GH_REPO" --json name --jq '[.[].name | select(startswith("AZURE_"))] | sort | join(",")')"
expect "production CONTAINER_APP" "$APP_PROD"    "$(gh variable get CONTAINER_APP --repo "$GH_REPO" --env production)"
expect "production PUBLIC_ORIGIN" "$PROD_ORIGIN" "$(gh variable get PUBLIC_ORIGIN --repo "$GH_REPO" --env production)"
expect "staging CONTAINER_APP"    "$APP_STG"     "$(gh variable get CONTAINER_APP --repo "$GH_REPO" --env staging)"
expect "staging PUBLIC_ORIGIN"    "$STG_ORIGIN"  "$(gh variable get PUBLIC_ORIGIN --repo "$GH_REPO" --env staging)"
echo "Phase 13 verified"
)
```

Then prove the deploy path end to end, staging first, with the tag already imported (this rebuilds
and `--force`-imports over the same tag, which is harmless before anyone relies on it):
`gh workflow run deploy.yml --repo fiscaltec/zendesk-mcp -f target=staging -f ref=main`.

---

## Phase 14 — Whole-estate verification

The checks for each origin, plus those only a finished estate can answer.

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
cd "$REPO_DIR"
for origin in "$PROD_ORIGIN" "$STG_ORIGIN"; do
  expect "$origin/health" 200 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "$origin/health")"
  expect "$origin/mcp unauthenticated" 401 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 -X POST "$origin/mcp" \
    -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}')"
  bash .github/scripts/verify-oauth-metadata.sh "$origin"
done
# What staging is actually SERVING. Exit 0 is the only pass; 2 is NOT ASSESSED, never a pass.
bash .github/scripts/check-serving-revisions.sh "$APP_STG" "$RG" \
  --env Authorization__ReadOnly --equals "$STG_READ_ONLY" --env ApplicationInsights__ConnectionString \
  --env Zendesk__TokenSecretPrefix --equals zendesk-staging-user-
bash .github/scripts/check-serving-revisions.sh "$APP_PROD" "$RG" \
  --env ApplicationInsights__ConnectionString --env Zendesk__TokenSecretPrefix --equals zendesk-user-
# Nothing left open from the build.
expect "KV public network access" Disabled "$(az keyvault show -g "$RG" -n "$KV" --query properties.publicNetworkAccess -o tsv)"
[ ! -e "$SECRET_DIR" ] && echo "OK   no secret files left in $SECRET_DIR" || fail "$SECRET_DIR still exists — rm -rf it"
echo "Phase 14 automated checks passed — now the sign-in checks below"
)
```

Then, by hand, for each origin (staging stays read-only, so use read tools):

1. Point an MCP client at `<origin>/mcp`, sign in with Microsoft. A wrong Entra secret passes every
   check above and fails **only** here, as `invalid_client` at `/oauth/token`.
2. Call `Get_zendesk_connection`, follow the connect link, connect your own Zendesk agent account.
3. Call a read tool (`List_brands`) and confirm it runs as you.
4. Confirm the audit record arrived — query the **workspace**, not `az monitor app-insights query`,
   which cannot resolve `AppEvents`:

```bash
( set -euo pipefail; source ~/zendesk-estate.env; assert_context
WS=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query customerId -o tsv)
n=$(az monitor log-analytics query -w "$WS" --analytics-query "AppEvents | where TimeGenerated > ago(1h) and Name == 'ZendeskToolCall' | summarize count() by AppRoleName" -o tsv)
nonempty "ZendeskToolCall records in the last hour, by target" "$n"
echo "$n"
)
```

An empty answer is a `FAIL`: either no tool call was made, or the exporter cannot authenticate
(Monitoring Metrics Publisher, Phase 4) or reach the AMPLS endpoint (Phase 2) — failures that are
silent in the app.

Finally, close out: drop the PIM elevations, and if any value in this runbook had to change to make
a step work, update `infra/terraform/` (and this file) to match in the same PR.

---

## Where the `az` build cannot mirror the Terraform word for word

| Terraform | Here | Why |
|---|---|---|
| `azurerm_subnet.app` delegation named `aca` | raw `az rest` PUT | `az … subnet create --delegations` names it `0` |
| `azurerm_monitor_private_link_scope` access modes | raw `az rest` PUT | `az monitor private-link-scope create` has no access-mode flags |
| `local_authentication_enabled = false` (workspace, App Insights) | `az resource update --set …` after create | neither create command has a flag for it |
| `azuread_application` + `_api_access` + `_pre_authorized` | one POST and four PATCHes through `az rest` | the ordering constraints; `az ad app create` cannot set the scope id, pre-authorisation or the self-reference |
| Owners = "the applying principal" | the signed-in operator | same meaning, different principal type |
| `azurerm_key_vault` with no `network_acls` block | the Phase 7 window sets `defaultAction: Deny` while open and restores what it found | an open window with the API default `Allow` would expose the vault to everyone |
| `lifecycle.ignore_changes` on the apps' image | n/a | `deploy.yml` owns the image after creation in both models |
| `oauth_shared_client_secret` / `teams_webhook_url` variables | captured into `/tmp`, never written to `~` or any state | there is no state; the vault is the record |
| Managed certificates, DNS, Graph app role, admin consent, secrets | the same out-of-band steps the README always had | unchanged |

## Unverified `az` behaviour

Checked offline against `az` 2.91 help and source where possible; **these could not be**, and are
the first suspects if a block fails:

- `az containerapp job create --yaml` with this exact shape (user-assigned identity keyed by resource
  id, `secretRef` env, `workloadProfileName` under `properties`). Cloud Shell may load the
  `containerapp` *extension* instead of the core module; neither loader has run this shape.
- `az role definition create` with `AssignableScopes` set to a single **registry**, as Terraform
  declares it. Microsoft documents a resource-level assignable scope as possible from the command
  line but *not recommended*, so expect it to be accepted; if it is refused, stop and decide —
  widening it to the Vitally RG changes the record.
- Graph `PATCH /applications` with the whole `api` object on each write — written so it works whether
  Graph merges or replaces, but not run.
- The Key Vault window from Cloud Shell: whether the vault sees Cloud Shell's traffic from the address
  `api.ipify.org` reports.
- `az containerapp env certificate list --managed-certificates-only` reporting
  `properties.subjectName`/`provisioningState` as queried.
- `gh variable get` (needs a recent `gh`; Cloud Shell's should have it).

Found by running it (2026-10-28, #3): `az network private-endpoint dns-zone-group show` answers `{}`
with exit 0 for a group that does not exist, so a `show … || create …` guard never creates it.
The three zone-group steps guard with `list` instead. Every other `show` used as a guard here was
checked against a missing name and fails as it should (exit 1 or 3).

Also found by running it (2026-10-29, #3): `az containerapp create --yaml` fills every field the YAML
leaves out with `null`, and the `2025-07-01` API that `az` 2.91 uses rejects `ingress.allowInsecure:
null` with `The JSON value could not be converted to System.Boolean` (preflight:
`ContainerAppInvalidSchema … Path: $`). Each of the other `null`s in the body was tested on its own
and is accepted. Phase 9.3 therefore sets `allowInsecure: false`, the azurerm default Terraform
relies on; the rest of the app YAML shape (user-assigned identity keyed by resource id,
`registries[].identity`, `secretRef` env) passed preflight.

Settled from the `az` 2.91 source, so no longer in doubt: `flow-log create` resolves the watcher from
`--location` and puts the flow log in its resource group (`NetworkWatcherRG`), and `--retention 30`
writes `{enabled: true, days: 30}`; `keyvault network-rule remove` compares addresses as networks,
so `<ip>/32` matches a rule stored without the suffix.

## Coverage: Terraform resource → step

Every `resource` in `infra/terraform/*.tf` (data sources and outputs excluded).

| Terraform resource | Step |
|---|---|
| `azurerm_resource_group.rg` | 1 |
| `azurerm_virtual_network.vnet` | 1 |
| `azurerm_subnet.app` | 1 (`az rest`) |
| `azurerm_subnet.pe` | 1 |
| `azurerm_subnet.pe_monitor` (`ampls.tf`) | 1 |
| `azurerm_public_ip.nat` | 1 |
| `azurerm_nat_gateway.nat` | 1 |
| `azurerm_nat_gateway_public_ip_association.nat` | 1 (`--public-ip-addresses` on create) |
| `azurerm_subnet_nat_gateway_association.app` | 1 (`natGateway` in the subnet PUT) |
| `azurerm_private_dns_zone.vault`, `.acr` | 1 |
| `azurerm_private_dns_zone_virtual_network_link.vault`, `.acr` | 1 |
| `azurerm_private_dns_zone.monitor` (×5) | 1 |
| `azurerm_private_dns_zone_virtual_network_link.monitor` (×5) | 1 |
| `azurerm_storage_account.flowlogs` | 1 |
| `azurerm_network_watcher_flow_log.vnet` | 1 |
| `azurerm_log_analytics_workspace.law` | 2 |
| `azurerm_application_insights.appi` | 2 |
| `azurerm_monitor_private_link_scope.ampls` | 2 (`az rest`) |
| `azurerm_monitor_private_link_scoped_service.law`, `.appi` | 2 |
| `azurerm_private_endpoint.ampls` | 2 |
| `azurerm_key_vault.secret` | 3 |
| `azurerm_private_endpoint.kv` | 3 |
| `azurerm_monitor_diagnostic_setting.kv` | 3 |
| `azurerm_user_assigned_identity.app` | 4 |
| `azurerm_federated_identity_credential.github_actions_prod`, `.github_actions_staging` | 4 |
| `azurerm_role_assignment.mi_kv_secrets_officer` | 4 |
| `azurerm_role_assignment.mi_acr_pull` | 4 |
| `azurerm_role_assignment.mi_monitoring_metrics_publisher` | 4 |
| `azurerm_role_assignment.mi_rg_containerapp_reader` | 4 |
| `azurerm_role_definition.acr_importer` | 4 |
| `azurerm_role_assignment.mi_acr_importer` | 4 |
| `azurerm_private_endpoint.acr` | 5 |
| `azuread_application.zendesk_mcp` | 6.1–6.3 |
| `azuread_application_api_access.msgraph`, `.self` | 6.4 |
| `azuread_application_pre_authorized.self` | 6.5 |
| `azuread_service_principal.zendesk_mcp` | 6.6 |
| `azuread_app_role_assignment.gate1` | 6.7 |
| `azuread_group.zendesk_readers`, `.zendesk_editors`, `.zendesk_admins` | 6.7 |
| `azurerm_container_app_environment.env` | 9.1 |
| `azurerm_monitor_diagnostic_setting.cae_system_logs` | 9.2 |
| `azurerm_container_app.app` | 9.3 |
| `azurerm_container_app.staging` | 9.3 |
| `azurerm_role_assignment.mi_ca_contributor`, `.mi_ca_staging_contributor` | 9.3 |
| `azurerm_container_app_job.scanner` | 9.4 (waits on the Teams webhook) |

Out-of-band in the Terraform as well, and built here: Entra client secret and both vault secrets (7),
first image (8), Graph `GroupMember.Read.All` and admin consent (10), custom domains and managed
certificates (11), tier membership (12), GitHub (13).

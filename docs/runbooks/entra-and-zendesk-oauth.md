# Entra app and Zendesk OAuth clients — set-up, rotation and user tokens

This server has **two OAuth clients**, and they are easy to confuse:

| | Entra app **Zendesk MCP** | Zendesk OAuth clients (one per target since 2026-11-04) |
|---|---|---|
| Signs people in to | this server (MCP clients → `/oauth/*` façade → Entra) | Zendesk, so the server can call it **as them** (`/zendesk/connect` → Zendesk → `/zendesk/callback`) |
| Defined in | `infra/terraform/entra.tf` | Zendesk Admin Centre → Apps and integrations → APIs → OAuth clients (not Terraform — Zendesk has no supported provider for it) |
| Client id lives in | `OAuth__SharedClientId` (Terraform reference) | `Zendesk__OAuthClientId`: production `fiscal_zendesk_mcp` (`zendesk_oauth_client_id`), staging `fiscal_zendesk_mcp_staging` (`staging_zendesk_oauth_client_id`) |
| Secret lives in | Key Vault `entra-mcp-client-secret`, **copied** into Container App secret `entra-oauth-client-secret` | Key Vault `zendesk-oauth-client-secret` (production) and `zendesk-staging-oauth-client-secret` (staging, via `Zendesk__OAuthClientSecretRef`), **read from the vault** by the app (cached 5 minutes) |
| Callback(s) | `https://zendesk-mcp.fiscaltec.com/oauth/callback`, `https://zendesk-mcp-staging.fiscaltec.com/oauth/callback` | one per client: production `https://zendesk-mcp.fiscaltec.com/zendesk/callback`; staging `https://zendesk-mcp-staging.fiscaltec.com/zendesk/callback` |
| Scopes | `openid profile email offline_access` + the API scope | allowed on the client and requested by the server: production `read write`; staging `read` (`Zendesk__OAuthScopes`) |

The Entra side is a straight port of vitally-mcp's. Its runbook,
[`docs/runbooks/entra-app-registration.md` in fiscaltec/vitally-mcp](https://github.com/fiscaltec/vitally-mcp/blob/main/docs/runbooks/entra-app-registration.md),
is the reference for the verified mechanics. Substitute the names in the table below; the procedure
does not change.

| vitally-mcp | zendesk-mcp |
|---|---|
| app `Vitally MCP` | app `Zendesk MCP` (`az ad app list --display-name "Zendesk MCP" --query "[].{appId:appId, objectId:id}"`) |
| `https://vitally.fiscaltec.com` | `https://zendesk-mcp.fiscaltec.com` |
| `vitally-prod-rg-uksouth`, `vitally-prod-ca-uksouth`, `vitally-staging-ca-uksouth` | `zendesk-prod-rg-uksouth`, `zendesk-prod-ca-uksouth`, `zendesk-staging-ca-uksouth` |
| `sg-vitally-*` | `sg-zendesk-*` |
| `docs/runbooks/rotate-helpers.sh` | the same file here, with `APP` and `RG` set for this app |

## Entra app

- **Ordering constraints**: these are the same two as vitally-mcp. Set `requestedAccessTokenVersion = 2` before
  `identifierUris`, and pre-authorise the scope in a separate write from the one that creates it.
  `entra.tf` already models both.
- **Gate 1 (sign-in)**: department groups are assigned **directly** to the app. Nesting does not
  grant sign-in. The list is `entra_gate1_group_object_ids` in `entra.tf`; since 2026-10-28 it
  admits the IT & Security Department only.
- **Admin consent**: grant tenant-wide consent for `openid profile email offline_access mcp.access`
  once, when the estate is built (`docs/runbooks/build-the-estate.md`, Phase 10): `az ad app permission admin-consent --id <appId>`.
- **Client secret**: create it out of band so it never lands in Terraform state. Store it in Key
  Vault as `entra-mcp-client-secret` with an expiry of 180 days or less, and copy it into both
  Container Apps as `entra-oauth-client-secret`. To rotate, follow vitally-mcp's verified procedure
  with `rotate-helpers.sh`. The two traps it records apply here exactly:
  - `az containerapp secret set` does not roll a revision. Production's warm replica keeps the old
    value until you restart it.
  - Staging (scale-to-zero) cannot rehearse that step.

## Zendesk OAuth clients

Each target has its **own** client (split on 2026-11-04; the build created one shared client). Each
lists only its own callback and has **allowed scopes** equal to what its target requests, so the
client caps what any token minted through it can do:

| | Production | Staging |
|---|---|---|
| Name (shown on the consent screen) | `FISCAL Zendesk MCP` | `FISCAL Zendesk MCP (staging)` |
| Unique identifier | `fiscal_zendesk_mcp` | `fiscal_zendesk_mcp_staging` |
| Redirect URL (only this) | `https://zendesk-mcp.fiscaltec.com/zendesk/callback` | `https://zendesk-mcp-staging.fiscaltec.com/zendesk/callback` |
| Allowed scopes = requested scopes | `read write` | `read` |
| Vault secret | `zendesk-oauth-client-secret` | `zendesk-staging-oauth-client-secret` |

Why: staging runs unreleased code against the one live Zendesk account. With a `read`-only client,
Zendesk itself refuses a write made with a staging token, even if `Authorization__ReadOnly` is lifted,
and a production connection can never be made through staging's callback. The cost: write checks
cannot run through staging; they are done on throwaway records through production.

Allowed scopes were checked against Zendesk's documentation on 2026-11-04 ([OAuth Clients API](https://developer.zendesk.com/api-reference/ticketing/oauth/oauth_clients/),
[granular scopes announcement](https://support.zendesk.com/hc/en-us/articles/11093380955674-Announcing-more-granular-OAuth-client-scopes)):
a request outside the list fails with `400 invalid_scope`, and the list applies to **new** tokens only.
`read` and `write` are Zendesk's broad scopes. Requesting granular scopes instead (`tickets:read`, …)
would be tighter still, but Zendesk does not document which scope each endpoint needs, so it would need
every tool re-validated live; not done.

### Create a client

1. Admin Centre → Apps and integrations → APIs → **OAuth clients** → Add OAuth client, with the
   values in the table above. **Client kind**: *Confidential* (the server holds the secret and uses
   PKCE as well). Set **allowed scopes** to exactly the target's row.
2. Copy the secret, shown once, and store it with *Writing a secret to the vault* below.
3. Point the target at it (`Zendesk__OAuthClientId`, and for staging `Zendesk__OAuthClientSecretRef`
   and `Zendesk__OAuthScopes`) with `az containerapp update --set-env-vars …`, then record it in
   `infra/terraform/` in the same PR. Existing connections on that target were made with the old
   client: their next refresh fails `invalid_grant` and each person gets a connect link.
4. Open a tool's connect link and connect a test agent. The consent screen should name the client and
   list only its scopes. A successful connect proves the client id, the secret, the redirect URL and
   the token endpoint together.

Tokens are requested as expiring (two-hour access tokens, 90-day refresh tokens) and refreshed
automatically; refresh and rotation were confirmed live on 2026-11-04 (#4).

### Writing a secret to the vault

The vault's data plane is private-endpoint only, and **no workstation has a working path to it**:
on the corporate VPN its DNS has no record for the private endpoint (`NXDOMAIN`), and off the VPN the
name resolves publicly and the vault refuses (`ForbiddenByConnection`, *"Public network access is
disabled"*). Found again on 2026-11-04, when a bare `az keyvault secret set` from WSL failed both
ways. So every write is a short, self-closing window, as `build-the-estate.md` Phase 7.2 does:
a temporary **Key Vault Secrets Officer** assignment for you (created only if you lack one, and
removed only if the block created it), `defaultAction: Deny`, an IP rule for your egress address,
public access on; a `trap` undoes all of it however the block ends. The role is an ordinary
assignment, not a PIM timer: if the shell is killed outright the trap cannot run, so check
afterwards (*Notes*).

Run it **off the VPN**, in WSL or a Linux shell, as one paste. Set `NAME` to the secret:

```bash
( set -euo pipefail
KV=zendesk-prod-kv-uksouth; RG=zendesk-prod-rg-uksouth; NAME=zendesk-staging-oauth-client-secret
ME=$(az ad signed-in-user show --query id -o tsv); KV_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
PREV=$(az keyvault show -g "$RG" -n "$KV" --query "properties.networkAcls.defaultAction" -o tsv); IP=""; CREATED=""
close_window() { set +e; unset SECRET; BAD=""
  # Public access off FIRST; the default action is restored only once that has succeeded, so a
  # failure here can never leave the vault open to every address (it stays Deny instead).
  if az keyvault update -g "$RG" -n "$KV" --public-network-access Disabled -o none; then OFF=1; else OFF=""; BAD=1; echo "!!! could not disable public access: default action left at Deny; disable public access by hand" >&2; fi
  [ -n "$IP" ] && { az keyvault network-rule remove -g "$RG" -n "$KV" --ip-address "$IP/32" -o none || { BAD=1; echo "!!! could not remove IP rule $IP" >&2; }; }
  [ -n "$OFF" ] && { az keyvault update -g "$RG" -n "$KV" --default-action "${PREV:-Allow}" -o none || { BAD=1; echo "!!! could not restore default action" >&2; }; }
  [ -n "$CREATED" ] && { az role assignment delete --assignee "$ME" --role "Key Vault Secrets Officer" --scope "$KV_ID" -o none || { BAD=1; echo "!!! could not remove your Secrets Officer role" >&2; }; }
  [ -z "$BAD" ] || { echo "!!! window NOT fully closed: fix the lines above by hand" >&2; exit 1; }
  echo "window closed"; }
trap close_window EXIT
if [ "$(az role assignment list --scope "$KV_ID" --assignee-object-id "$ME" --query "length([?roleDefinitionName=='Key Vault Secrets Officer'])" -o tsv)" != 1 ]; then
  az role assignment create --assignee-object-id "$ME" --assignee-principal-type User --role "Key Vault Secrets Officer" --scope "$KV_ID" -o none; CREATED=1; fi
EGRESS=$(curl -4 -fsS https://api.ipify.org); case "$EGRESS" in *.*.*.*) echo "your egress IP: $EGRESS";; *) echo "bad IP '$EGRESS'" >&2; exit 1;; esac
az keyvault update -g "$RG" -n "$KV" --default-action Deny -o none
az keyvault network-rule add -g "$RG" -n "$KV" --ip-address "$EGRESS/32" -o none; IP=$EGRESS
az keyvault update -g "$RG" -n "$KV" --public-network-access Enabled -o none
ok=""; last=""
for i in $(seq 1 20); do last=$(timeout 30 az keyvault secret list --vault-name "$KV" -o none 2>&1) && { ok=1; break; }; echo "  not reachable yet (attempt $i): ${last:0:120}"; sleep 15; done
[ -n "$ok" ] || { echo "vault unreachable after 5 minutes: nothing stored" >&2; exit 1; }
read -rsp "Paste the secret (hidden): " SECRET; echo
[ -n "$SECRET" ] || { echo "empty: nothing stored" >&2; exit 1; }
printf %s "$SECRET" | az keyvault secret set --vault-name "$KV" --name "$NAME" --file /dev/stdin --expires "$(date -u -d '+180 days' +%Y-%m-%dT%H:%M:%SZ)" -o none
want=${#SECRET}; got=$(az keyvault secret show --vault-name "$KV" -n "$NAME" --query "length(value)" -o tsv)
[ "$want" = "$got" ] && echo "stored: length $got matches" || { echo "!!! stored length $got, pasted $want" >&2; exit 1; } )
```

Run on 2026-11-04 to store `zendesk-staging-oauth-client-secret` (Key Vault `SecretSet` 200, and
afterwards public access `Disabled`, no IP rule, no role left behind). Since then it removes the role
only if it created it, and records the IP rule only once it is added. Notes:

- `printf %s`, never a `<<<` here-string: a here-string appends a newline, `--file` stores it, and
  every connect, refresh and revoke then fails with `invalid_client`. The length check catches it.
- If clean-up fails part-way, the block says which step failed, prints *window NOT fully closed* and
  exits non-zero. Public access is switched off **before** the default action is restored, so a
  failure there leaves the vault on `Deny`, never open to every address.
- Each probe is capped with `timeout 30` and prints its error, so a network refusal, a DNS failure
  and a role still propagating can be told apart. An earlier version hid the errors and appeared to
  hang.
- You need rights to create role assignments on the vault (the PIM role used for the build).
- Check afterwards, from anywhere (management plane): `az keyvault show -g zendesk-prod-rg-uksouth -n
  zendesk-prod-kv-uksouth --query "{public:properties.publicNetworkAccess, ips:properties.networkAcls.ipRules}"`
  must show `Disabled` and no rules, and `az role assignment list --scope <vault id>` only the
  managed identity's Secrets Officer.

### Rotate a client secret

The app reads its client secret from the vault on demand and caches it for five minutes. Unlike the
Entra secret, rotating the vault value **is** the rotation. Per client:

1. In Admin Centre, regenerate the client's secret. The old one stops working at once, so do steps 1
   and 2 together, out of hours.
2. Store the new value under that client's vault secret (table above) with *Writing a secret to the
   vault*. Within five minutes every replica of that target uses it.
3. Restart the revision if you cannot wait five minutes. Connect, refresh and revoke fail with
   `invalid_client` until the cache expires.

People's existing tokens are **not** affected by a client-secret rotation. The secret is only used
to connect and to refresh.

## People's tokens

Each connected person has one Key Vault secret:

- `zendesk-user-<Entra object id>` in production;
- `zendesk-staging-user-<object id>` in staging.

The value is JSON: access token, refresh token, expiries, and the Zendesk user id, email and role
confirmed at connect time. Tag `kind=zendesk-user-token`. The secret-expiry scanner skips these: a
refresh-token expiry is self-healing, because the person is asked to reconnect.

| Task | How |
|---|---|
| See who is connected | `az keyvault secret list --vault-name … --query "[?tags.kind=='zendesk-user-token' && tags.revoked!='true'].name"` |
| Disconnect one person | They can run *Disconnect_zendesk*. As an admin, write a tombstone version: `az keyvault secret set … --name zendesk-user-<oid> --value '{"revoked":true}' --tags kind=zendesk-user-token revoked=true`, **and** revoke at Zendesk (below). |
| Revoke at Zendesk | Suspend the agent or change their role, or delete the token under Admin Centre → APIs → OAuth tokens. Zendesk then answers `401`, and the server forgets the token and offers a fresh link. |
| Disconnect everyone | Regenerate the client secret (above) **and** revoke the client's tokens in Admin Centre. Then tombstone the secrets, or leave them: a revoked token is refused at Zendesk and forgotten on first use. |

⚠️ **Never delete these secrets.** Soft delete is on and purge protection holds the name for 90
days. A deleted name cannot be set again, so that person could not reconnect until the purge window
passed. A tombstone version reads as "not connected" and keeps the name usable. Older versions still
hold the old token, which is why revoking at Zendesk comes first.

## Read-only and the feature switches

- `Authorization__ReadOnly=true` denies every create and update for everyone and hides the write
  tools, except *Disconnect_zendesk*: people can still cut their own connection during a lock-down.
  Staging always runs with it on. Since 2026-11-04 it is the server's guard behind Zendesk's own:
  staging's client allows only `read`, so lifting it alone no longer makes staging write. See vitally-mcp's `read-only-and-rbac-rollout.md` for why this was
  the safest staging posture there.
- `Zendesk__Features__AllowPublicReplies`, `AllowArticlePublishing`, `AllowHelpCenterStructureChanges`, `AllowMacroChanges` and
  `AllowUserAndOrganizationChanges` are account-wide. The application defaults them off; FISCAL's
  Terraform turns all five on for both targets (decided 2026-10-28). Turning one off, or back on,
  is a decision to record in the PR or issue that does it, with who approved it.

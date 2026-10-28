# Entra app and Zendesk OAuth client — set-up, rotation and user tokens

This server has **two OAuth clients**, and they are easy to confuse:

| | Entra app **Zendesk MCP** | Zendesk OAuth client |
|---|---|---|
| Signs people in to | this server (MCP clients → `/oauth/*` façade → Entra) | Zendesk, so the server can call it **as them** (`/zendesk/connect` → Zendesk → `/zendesk/callback`) |
| Defined in | `infra/terraform/entra.tf` | Zendesk Admin Centre → Apps and integrations → APIs → OAuth clients (not Terraform — Zendesk has no supported provider for it) |
| Client id lives in | `OAuth__SharedClientId` (Terraform reference) | `Zendesk__OAuthClientId` (Terraform variable `zendesk_oauth_client_id`) |
| Secret lives in | Key Vault `entra-mcp-client-secret`, **copied** into Container App secret `entra-oauth-client-secret` | Key Vault `zendesk-oauth-client-secret`, **read from the vault** by the app (cached 5 minutes) |
| Callback(s) | `https://zendesk-mcp.fiscaltec.com/oauth/callback`, `https://zendesk-mcp-staging.fiscaltec.com/oauth/callback` | `https://zendesk-mcp.fiscaltec.com/zendesk/callback`, `https://zendesk-mcp-staging.fiscaltec.com/zendesk/callback` |

The Entra side is a straight port of vitally-mcp's. Its runbook,
[`docs/runbooks/entra-app-registration.md` in fiscaltec/vitally-mcp](https://github.com/fiscaltec/vitally-mcp/blob/main/docs/runbooks/entra-app-registration.md),
is the reference for the verified mechanics. Substitute the names in the table below; the procedure
does not change.

| vitally-mcp | zendesk-mcp |
|---|---|
| app `Vitally MCP` | app `Zendesk MCP` (`terraform output entra_app_client_id`, objectId `entra_app_object_id`) |
| `https://vitally.fiscaltec.com` | `https://zendesk-mcp.fiscaltec.com` |
| `vitally-prod-rg-uksouth`, `vitally-prod-ca-uksouth`, `vitally-staging-ca-uksouth` | `zendesk-prod-rg-uksouth`, `zendesk-prod-ca-uksouth`, `zendesk-staging-ca-uksouth` |
| `sg-vitally-*` | `sg-zendesk-*` |
| `docs/runbooks/rotate-helpers.sh` | the same file here, with `APP` and `RG` set for this app |

## Entra app

- **Ordering constraints**: these are the same two as vitally-mcp. Set `requestedAccessTokenVersion = 2` before
  `identifierUris`, and pre-authorise the scope in a separate write from the one that creates it.
  `entra.tf` already models both.
- **Gate 1 (sign-in)**: department groups are assigned **directly** to the app. Nesting does not
  grant sign-in. The list is `entra_gate1_group_object_ids`, which has no default until the
  departments are decided.
- **Admin consent**: grant tenant-wide consent for `openid profile email offline_access mcp.access`
  once, after the first apply: `az ad app permission admin-consent --id <appId>`.
- **Client secret**: create it out of band so it never lands in Terraform state. Store it in Key
  Vault as `entra-mcp-client-secret` with an expiry of 180 days or less, and copy it into both
  Container Apps as `entra-oauth-client-secret`. To rotate, follow vitally-mcp's verified procedure
  with `rotate-helpers.sh`. The two traps it records apply here exactly:
  - `az containerapp secret set` does not roll a revision. Production's warm replica keeps the old
    value until you restart it.
  - Staging (scale-to-zero) cannot rehearse that step.

## Zendesk OAuth client

### Create

1. Admin Centre → Apps and integrations → APIs → **OAuth clients** → Add OAuth client.
   - **Name**: `FISCAL Zendesk MCP`. Agents see this name on the consent screen and in their
     authorised apps list.
   - **Unique identifier**: for example `fiscal_zendesk_mcp`. This value becomes `zendesk_oauth_client_id`.
   - **Client kind**: *Confidential*. The server holds the secret and uses PKCE as well.
   - **Redirect URLs**: both `/zendesk/callback` URLs in the table above, one per line.
2. Copy the secret, which is shown once, straight into the vault. Do it from a host with a path to the
   private endpoint, and never echo the value:

   ```bash
   az keyvault secret set --vault-name zendesk-prod-kv-uksouth --name zendesk-oauth-client-secret \
     --file /dev/stdin --expires "$(date -u -d '+180 days' +%Y-%m-%dT%H:%M:%SZ)" <<< "$SECRET"
   unset SECRET
   ```

3. Set `zendesk_oauth_client_id` and apply. Then open `/zendesk/connect` through a tool's link and
   connect a test agent. A successful connect proves the client id, the secret, the redirect URL and
   the token endpoint together.

The scopes requested are `read write` (`Zendesk:OAuthScopes`). Tokens are requested as expiring
(two-hour access tokens, 90-day refresh tokens) and are refreshed automatically. ⚠️ Unverified until
staging: that this Zendesk account issues refresh tokens for the requested `expires_in`. If it
returns a non-expiring token instead, the server stores it without an expiry, and nothing breaks.

### Rotate the client secret

The app reads `zendesk-oauth-client-secret` from the vault on demand and caches it for five minutes.
Unlike the Entra secret, rotating the vault value **is** the rotation.

1. In Admin Centre, regenerate the client's secret. The old one stops working at once, so do steps 1
   and 2 together, out of hours.
2. Set the new value in the vault, as above. Within five minutes every replica uses it.
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
  tools. Use it on staging whenever it is pointed at production Zendesk data and nobody is
  deliberately testing writes. See vitally-mcp's `read-only-and-rbac-rollout.md` for why this was
  the safest staging posture there.
- `Zendesk__Features__AllowPublicReplies`, `AllowArticlePublishing`, `AllowHelpCenterStructureChanges`, `AllowMacroChanges` and
  `AllowUserAndOrganizationChanges` are account-wide and default off. Turning one on is a decision
  to record in the PR or issue that does it, with who approved it.

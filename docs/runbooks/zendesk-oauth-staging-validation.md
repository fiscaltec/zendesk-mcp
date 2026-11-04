# Staging validation — Entra sign-in and per-user Zendesk OAuth (#4)

`https://zendesk-mcp-staging.fiscaltec.com` is the pre-production target. This is the part of the
acceptance suite that needs a real sign-in, and therefore a person, plus a reader of the logs. Re-run
it whenever the Entra app, the Zendesk OAuth clients, the token store or the connect flow changes.

It was first run end to end on 2026-11-04 (#4), partly on production before staging could do every
step; every expected result below is what that run actually showed.

**What staging can and cannot touch.** Staging calls the one live Zendesk account (`fiscalsupport`;
there is no sandbox), as the signed-in person. Two independent things stop it writing:

- `Authorization__ReadOnly=true` (the server's guard), standing on staging;
- its own Zendesk OAuth client, `fiscal_zendesk_mcp_staging`, whose allowed scopes are `read` only,
  so Zendesk refuses a write made with a staging token even if the guard were lifted.

Nothing in this runbook lifts either, and nothing here changes production. Step 3 changes **your own**
Entra group membership for a few minutes and restores it.

## Before you start

- An MCP client with a server entry for staging, e.g. Claude Code:
  `claude mcp add --transport http zendesk-staging https://zendesk-mcp-staging.fiscaltec.com/mcp`.
- A Zendesk **agent or admin** account whose email equals your Microsoft sign-in name, and for step 2
  a **second** Zendesk login that is **not** you (an end user, or ideally another agent).
- For the log checks: your PIM role active and `az login` done, in a shell where `az` reaches
  `management.azure.com` (Cloud Shell, WSL or a cloud session). The queries go through the ARM
  `api/query` endpoint, so they need no `log-analytics` extension (its query command is preview-only).

Define the query helper once per shell. It prints the result rows as tab-separated text:

```bash
LAW_ID=$(az monitor log-analytics workspace show -g zendesk-prod-rg-uksouth -n zendesk-prod-law-uksouth --query id -o tsv)
q() { local body; body=$(python3 -c 'import json,sys; print(json.dumps({"query": sys.argv[1], "timespan": "P1D"}))' "$1")
  az rest --method post --url "https://management.azure.com$LAW_ID/api/query?api-version=2020-08-01" --body "$body" --query "Tables[0].Rows" -o tsv; }
q 'AppEvents | where TimeGenerated > ago(1d) | summarize count() by AppRoleName'   # a smoke test: one row per target that saw traffic
```

The response uses capitalised `Tables`/`Rows`; lower-case returns nothing, which is easy to misread as
"no records". A `ws_closed_mid_exchange` / connection reset from a cloud session's proxy is transient:
re-run the query once.

**Log latency.** Application Insights rows arrive within a minute or two. **Key Vault diagnostics
(`AzureDiagnostics`) take 5–10 minutes**: wait before reading an absence as a failure.

**Cold start.** Staging scales to zero. If your client reports the server as failed or never
connecting, it most likely timed out during the cold start: reconnect it (Claude Code: `/mcp` →
the server → *Reconnect*) and it will be up.

Throughout, `<oid>` is your Entra object id (`az ad user show --id <you>@fiscaltec.com --query id -o tsv`).

## 1 — Sign in and connect

1. In the client, call any Zendesk tool on staging, e.g. `List_brands`. The client opens a Microsoft
   sign-in first if it has no token; complete it.
2. The tool answers with a **connect link** (`…/zendesk/connect?ticket=…`). Open it in your normal
   browser, sign in to Zendesk as yourself, and click **Allow**.
   - The consent screen must name **FISCAL Zendesk MCP (staging)** and ask for **read** access only.
3. Call `List_brands` again: it returns the account's brands.

Verify:

```bash
q 'AppTraces | where TimeGenerated > ago(1h) and AppRoleName == "zendesk-staging-ca-uksouth" and Message has "oauth/authorizations/new" | project TimeGenerated, cid=extract("client_id=([^&]+)",1,Message), scope=extract("scope=([^&]+)",1,Message) | order by TimeGenerated asc'
q 'AppTraces | where TimeGenerated > ago(1h) and AppRoleName == "zendesk-staging-ca-uksouth" and Message has "Connected Zendesk user" | project TimeGenerated, Message'
```

Expect `fiscal_zendesk_mcp_staging` and `read` on every redirect, and `Connected Zendesk user <zendesk
id> for <oid>`. A redirect naming `fiscal_zendesk_mcp` means staging is pointed at production's
client: stop and check `Zendesk__OAuthClientId` on `zendesk-staging-ca-uksouth`.

## 2 — A login that is not yours is refused and revoked

1. Call `Get_zendesk_connect_link` on staging.
2. Open the link in a **new private window** and complete the whole flow there, signing in to Zendesk
   with the **other** login and clicking **Allow**.
3. Expect a page titled **Connection refused** (HTTP 403). Its sentence names the reason: *Only Zendesk
   agents and admins can connect…* for an end user, or an email-mismatch sentence for another agent.
4. `Get_zendesk_connection` must still show **your** account: a refused login never replaces a stored one.

Verify (the refusal is a Warning; the revoke logs only if it fails):

```bash
q 'AppTraces | where TimeGenerated > ago(1h) and AppRoleName == "zendesk-staging-ca-uksouth" and (Message has "Refused a Zendesk connection" or Message has "revocation returned" or Message has "status code 40") | project TimeGenerated, SeverityLevel, Message'
```

Expect `Refused a Zendesk connection for <oid>: <reason> (Zendesk user <other id>)` and a `403`, and
**no** `Zendesk token revocation returned …` (that line means the refused token was left live: revoke
it in Admin Centre → APIs → OAuth tokens).

⚠️ A **400** page (*Connection not completed*, *Access was not granted*, *This link has expired*)
means the flow stopped **before** the identity check: the sign-in finished in another window, took
over 15 minutes, or **Deny** was clicked. It proves nothing about the refusal; run step 2 again. This
happened once on 2026-11-04 and was nearly recorded as a pass.

The **email-mismatch** branch needs a second login that is an **agent** with a different email. On
2026-11-04 only an end user was available, so only the role branch was seen live; the mismatch branch
shares the refuse-and-revoke code and is covered by `ZendeskConnectEndpointsTests`.

## 3 — Tiers: a reader sees and gets only read tools

Either have a colleague who holds only the **read** tier do this step, or move yourself to the read
tier for a few minutes. To move yourself (needs a role that can change group membership):

```bash
ME=$(az ad user show --id <you>@fiscaltec.com --query id -o tsv)
for g in sg-zendesk-readers sg-zendesk-editors sg-zendesk-admins; do printf '%s direct=%s\n' $g "$(az ad group member check -g $g --member-id $ME --query value -o tsv)"; done   # note these
az ad group member remove -g sg-zendesk-editors --member-id $ME; az ad group member remove -g sg-zendesk-admins --member-id $ME
```

Wait a minute (`Authorization:LiveGroupCacheSeconds` is 60), reconnect the server in the client so it
re-reads `tools/list`, then:

1. List the server's tools. Expect **only** read tools — `Get_`/`List_`/`Search_`/`Count_`/`Find_`/
   `Preview_`/`Export_`, plus `Get_zendesk_connect_link` and `Disconnect_zendesk` — **28** of 45 at
   the time of writing. No `Create_`, `Update_`, `Set_`, `Apply_`, `Add_` or `Request_` tool.
2. Run a read, e.g. *search for three tickets created this week*.

Verify the tier the server used:

```bash
q 'AppEvents | where TimeGenerated > ago(1h) and Name == "ZendeskToolCall" and tostring(Properties.AuditUserId) == "<oid>" | project TimeGenerated, AppRoleName, tool=tostring(Properties.McpToolName), tier=tostring(Properties.AuditPermissionTier), outcome=tostring(Properties.AuditOutcome)'
```

Expect `zendesk:read` alone (an editor or admin shows `zendesk:delete,zendesk:read,zendesk:write`).
**Restore** the groups you noted:

```bash
az ad group member add -g sg-zendesk-editors --member-id $ME; az ad group member add -g sg-zendesk-admins --member-id $ME
```

Zendesk's **own** restrictions (an agent limited to some groups sees only their tickets) are a
separate check that needs a non-admin agent: #6.

## 4 — Refresh past expiry, and the rotated token is stored

Leave the connection unused for **more than two hours** (access tokens are requested at 2 h), then call
`List_brands`. It must succeed with **no** connect link. `Get_zendesk_connection` does not prove this:
it reads the stored grant without calling Zendesk.

Verify the refreshed token was **persisted**, not only cached (wait 10 minutes for Key Vault logs):

```bash
q 'AzureDiagnostics | where ResourceProvider == "MICROSOFT.KEYVAULT" and TimeGenerated > ago(1h) and id_s has "zendesk-staging-user-<oid>" | project TimeGenerated, OperationName, httpStatusCode_d | order by TimeGenerated asc'
```

Expect a `SecretSet` 200 seconds before the successful call. A refused refresh logs
`Zendesk refused to refresh the token for <oid> (<error code>); forgetting the grant` and returns a
connect link.

## 5 — The audit records carry the right content

```bash
q 'AppEvents | where TimeGenerated > ago(1h) and AppRoleName == "zendesk-staging-ca-uksouth" and Name startswith "Zendesk" | project TimeGenerated, Name, p=Properties | order by TimeGenerated asc'
```

For each tool call expect a `ZendeskToolCall` with `AuditUserId` = `<oid>`, `McpToolName`,
`McpToolArguments`, `AuditRecordIds` (a search returns one id per record; `List_brands` the brand id),
`AuditPermissionTier`, `McpClientName` and `AuditOutcome`, and one `ZendeskUpstreamCall` per Zendesk
request sharing its `AuditCorrelationId`. No row may contain a Zendesk response body. The denial
records and a negative control are still open in #5.

## 6 — Disconnect revokes and tombstones; reconnect works

1. Call `Disconnect_zendesk` on staging and note the time. Expect `result: disconnected`.
   `forgotten_not_revoked` means Zendesk did not confirm the revoke: remove the token in Admin Centre
   → APIs → OAuth tokens.
2. `List_brands` now answers with a connect link.
3. Reconnect through it; `List_brands` works again.

Verify (Key Vault: wait 10 minutes):

```bash
q 'AzureDiagnostics | where ResourceProvider == "MICROSOFT.KEYVAULT" and TimeGenerated > ago(1h) and id_s has "zendesk-staging-user-<oid>" | project TimeGenerated, OperationName, httpStatusCode_d | order by TimeGenerated asc'
q 'AppTraces | where TimeGenerated > ago(1h) and AppRoleName == "zendesk-staging-ca-uksouth" and Message has "revocation returned" | project TimeGenerated, Message'
```

Expect a `SecretGet` then a `SecretSet` 200 at the disconnect (the tombstone; two `SecretSet`s if the
access token had expired and was refreshed first), **no** `SecretDelete` — a delete would block
reconnecting for the 90-day purge window — and no revocation warning. On 2026-11-04 this showed that a
`read`-only token can revoke itself.

## If something fails

| Symptom | Likely cause |
|---|---|
| The client never connects to staging | cold start; reconnect it |
| A Microsoft sign-in is asked for again | the client holds no valid Entra token for this server; complete it. Not a Zendesk fault |
| Every tool answers with a connect link right after connecting | the stored token is not being read: check Key Vault `SecretGet`s and the `Zendesk__TokenSecretPrefix` (`zendesk-staging-user-`) |
| `invalid_scope` from Zendesk at sign-in | the server requests a scope the client does not allow: compare `Zendesk__OAuthScopes` (`read`) with the client's allowed scopes |
| `invalid_client` at connect, refresh or revoke | the vault secret does not match the client's secret (often a stored trailing newline): store it again with *Writing a secret to the vault* in `entra-and-zendesk-oauth.md` |
| A write tool appears on staging | `Authorization__ReadOnly` is not `true` on the serving revision: restore it before anything else |

## After it passes

Record the date, the times and the evidence on the issue that asked for the run, and update the
*Staging validation* table in `CLAUDE.md`.

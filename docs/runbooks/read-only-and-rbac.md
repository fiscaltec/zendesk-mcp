# Read-only mode and the permission tiers

Adapted from vitally-mcp's `docs/runbooks/read-only-and-rbac-rollout.md` (audit #10). Its rollout
history (the claim tier and the previous identity provider) does not apply here: this server had live
Entra group tiers from its first deploy.

## `Authorization__ReadOnly`: what it does

When `Authorization__ReadOnly=true` is set on the Container App revision:

- Every non-GET Zendesk call is denied for everyone (`ToolAuthorizer`, before the tier check), and the
  denial is audited.
- `tools/list` shows only tools annotated read-only, plus `Disconnect_zendesk`
  (`ReadOnlyToolFilter.OwnConnectionTools`). That tool touches only the caller's own connection, and
  a lock-down is when someone most needs to cut it.
- It is independent of Graph, the token and group membership: a lock that consults nothing.

**It is deployment-wide and overrides per-caller filtering.** `tools/list` is also filtered per
caller by tier. With `ReadOnly` on, readers, editors and admins all see the same read tools. That
is the switch working, not the filtering breaking. (There are no delete tools, so editors and admins
see the same list even with it off.)

## Its live use: guarding staging

Staging acts on the one live Zendesk account. There is no sandbox. `Authorization__ReadOnly=true`
is staging's standing setting (`var.staging_read_only`, default `true`, in
`infra/terraform/containerapps-staging.tf`). Lift it only for a deliberate check that needs the
write tools to be visible, such as proving a reader is denied one.

PR #27 gives staging its own Zendesk OAuth client, which may request only the `read` scope. Once
that is merged, Zendesk itself refuses a staging write even with this switch lifted, so the switch
becomes the second layer rather than the only one. vitally-mcp could not do the same, because Vitally
offers no read-scoped key.

⚠️ **A recreate does NOT bring staging up guarded.** `containerapps-staging.tf` records the setting,
but nothing applies the file: the Terraform here is a record and is never applied. An app created
any other way starts on the application default, `false`. The same is true of
`ApplicationInsights__ConnectionString`: without it, the audit records stay on stdout instead of
reaching `AppEvents`. Neither failure announces itself. After any recreate, set both, then verify
what staging is **serving**:

```bash
bash .github/scripts/check-serving-revisions.sh zendesk-staging-ca-uksouth zendesk-prod-rg-uksouth \
  --env Authorization__ReadOnly --equals true --env ApplicationInsights__ConnectionString \
  --env Zendesk__TokenSecretPrefix --equals zendesk-staging-user-
```

This is the staging check in `docs/runbooks/build-the-estate.md` Phase 14, which was run when the
estate was built (#3), with `true` written in. Only exit **0** with `PASS` is a pass:

| Output | Means |
|---|---|
| exit 1 | An assertion definitely failed; `<unset>` there means **unguarded**, not "defaulted to safe" |
| `NOT ASSESSED` (exit 2) | You do not know, which is not a pass |
| exit 3 | `az` says the app does not exist |

A `secretRef`-backed variable counts as set; the script's header says why a `.value` query would
get that wrong. `ApplicationInsights__ConnectionString` being set means **configured, not
exporting**: only an `AppEvents` query proves ingestion. The script reads every revision that can
serve, not the desired template, which reports the new value while the previous revision is still
answering requests.

Toggling the switch rolls a new revision, and a new revision is a new process. That empties
everything held in memory:

- the Graph permission cache;
- the cached tokens;
- every outstanding connect link and connect attempt, and every upload slot (single replica, in
  memory; see `CLAUDE.md`).

Anyone part-way through connecting Zendesk, or uploading, starts again.

## What it is not for

**It is not a lever during a Microsoft Graph outage.** Setting it means a new revision, and the
permission cache is in process (`AddMemoryCache`). With Graph unreachable, a fresh process can resolve
nobody, so every caller is denied everything, reads included, whatever this flag says. What covers a
Graph outage is `Authorization__LiveGroupStaleSeconds`, which serves each caller's last known-good
tier from the running process. A restart throws that away. To stop the server, use
`az containerapp ingress disable`, and mean it.

## The tiers: how they are granted, and how to check them

| Tier | Entra group | What it allows |
|---|---|---|
| `zendesk:read` | `sg-zendesk-readers` | GETs |
| `zendesk:write` | `sg-zendesk-editors` | POST, PUT, PATCH |
| `zendesk:delete` | `sg-zendesk-admins` | DELETE (no tool issues one today) |

`ACCESS.md` is the canonical user-facing description.

- **Tiers are transitive.** `Authorization__LiveGroupCheck=true` resolves live membership through
  Graph `transitiveMembers`, keyed on the caller's `oid`. A department group nested inside an
  `sg-zendesk-*` group grants that tier.
- **Sign-in is direct only.** The *Zendesk MCP* enterprise app has `appRoleAssignmentRequired = true`,
  which honours only **direct** members of assigned groups, so nesting does not grant sign-in. Each
  department must be assigned to the app directly (`entra_gate1_group_object_ids` in
  `infra/terraform/entra.tf`) **as well as** nested into its tier. That list is the only record of who
  can sign in, and nothing detects an omission.
- **No token claim authorises anyone.** With `LiveGroupCheck` on, the order is fresh Graph, then
  stale Graph (up to `LiveGroupStaleSeconds`), then deny. Do not add a claim fall-through as a safety
  net: one that can only ever deny reads like a working fallback and behaves like a silent denial
  (vitally-mcp#108).
- **A Zendesk role is checked too.** Every call is made as the person's own Zendesk agent, so Zendesk
  can still refuse what the tier allows.

**To check the tiers on a live revision** (needs a real sign-in per tier; #4's runbook carries the
steps): a reader calling a write tool is denied; an editor's writes succeed. Look for the denial in
`AppEvents` keyed by the caller's Entra **object id**, and expect a **`ZendeskToolCallDenied`**
record (`LogToolCallDenied`), not `ZendeskUpstreamDenied` (`LogDenied`). The SDK's authorisation filter rejects an out-of-tier call at the per-tool `[Authorize]`
check, before `ZendeskService.SendAsync` runs, and `LogDenied` is reached only from inside
`SendAsync`. If you look for the wrong event, you cannot tell it apart from the denial not being
audited at all. (A read-only refusal is the other way round: the tool is allowed, its non-GET call
is refused inside `SendAsync`, so it is a `ZendeskUpstreamDenied`.)

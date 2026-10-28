# Zendesk MCP — Access & Connection Guide

How to connect to the Zendesk MCP server, and how access is granted.

> **Not yet live.** The hostname below is the proposed one, and the department list and group
> object ids are filled in once the Entra objects exist (`infra/terraform/entra.tf` outputs them).

## Connecting

Point your MCP client at:

```
https://zendesk-mcp.fiscaltec.com/mcp
```

Connecting is two sign-ins, once each:

1. **Microsoft.** Your client opens a Microsoft sign-in the first time you connect. This signs you in
   to the server and decides which tools you get (see *Access model*).
2. **Zendesk.** The first time you use a Zendesk tool, it answers with a one-time link instead of a
   result. Open it, sign in to Zendesk **as yourself**, approve access, and then ask Claude to try
   again. From then on every Zendesk call is made as your own Zendesk agent, so your Zendesk role,
   groups and brand access apply, and ticket updates are attributed to you.

The Zendesk link is refused if:

- the Zendesk login's email differs from your Microsoft account's;
- it is an end-user (customer) login rather than an agent or admin;
- it was opened in a different browser from the one that started the sign-in;
- it has already been used, or more than 15 minutes have passed.

These checks are what stop someone else's Zendesk account being attached to your identity. If you
are signed in to Zendesk as someone else, sign out first.

| Client | How to connect |
|---|---|
| Claude Desktop / claude.ai | Once published as a FISCAL organisation connector: Connectors → **FISCAL Zendesk MCP** → Connect. Until then: Settings → Connectors → Add custom connector → paste the URL |
| Claude Code | `claude mcp add --transport http fiscal-zendesk https://zendesk-mcp.fiscaltec.com/mcp` — inherits the claude.ai connector instead once that is published |
| VS Code / Cursor / other | Add an MCP server entry pointing at the URL; the client handles the Microsoft sign-in |

**Your connection:** ask Claude to run *Get_zendesk_connection* to see which Zendesk account you are
connected as. *Get_zendesk_connect_link* gives you a new link (for example, to switch accounts).
*Disconnect_zendesk* revokes the token at Zendesk and removes it from this server. Connections made
through staging are separate from production's.

### Not the same as Swifteq's Zendesk connector

Swifteq's hosted Zendesk MCP server offers a similar set of tools with similar names. It is a
separate service with its own Zendesk connection and its own permissions. If both are enabled in your
client, disable the one you are not using, so that tool approvals and wildcard permissions apply to
the server you intend.

### "It connected, but there are no tools"

You signed in to Microsoft but hold no tier: the server shows a caller with no `sg-zendesk-*`
membership an empty tool list. Ask for access (below). If you can see tools but every call answers
with a connect link, that is the Zendesk step — open the link.

## Access model

Authentication alone grants nothing. Every action is checked against your permission tier, derived
from your **Microsoft Entra group membership**, and then against your **Zendesk role**, because the
call is made as your Zendesk agent:

| Tier | Permission | What you can do | Entra group |
|---|---|---|---|
| *(none)* | — | Nothing — an empty tool list | — |
| Read | `zendesk:read` | Search and read tickets, conversations, reports, macros, users, organisations, configuration and the Help Center | `sg-zendesk-readers` |
| Write | `zendesk:write` | Read **+** create and update tickets, apply and manage macros, organisation memberships, Help Center content, uploads | `sg-zendesk-editors` |
| Delete | `zendesk:delete` | Read + write (no delete tools exist in this version) | `sg-zendesk-admins` |

Tiers are cumulative. You need only the highest tier you require. Your client is shown only the tools
your tier permits.

**Zendesk still decides what your agent can do.** An editor whose Zendesk role cannot edit macros,
or cannot see a group's tickets, gets Zendesk's refusal through this server. Four account-wide
switches also apply to everyone, whatever their tier:

| Switch | Covers |
|---|---|
| Public replies | public comments, CCs and public macro replies (they email customers) |
| Article publishing | publishing Help Center articles and translations rather than saving drafts |
| Macro changes | creating, editing, activating and deactivating macros |
| User and organisation changes | creating end users from an email address, organisation memberships |

All four are **off** until the Infrastructure team turns them on (`Zendesk__Features__*`).

Group object ids (filled in from Terraform outputs once created):

| Group | Object ID |
|---|---|
| `sg-zendesk-readers` | *pending* |
| `sg-zendesk-editors` | *pending* |
| `sg-zendesk-admins`  | *pending* |

### Signing in is gated too (department group)

Before a tier applies you must be able to **authenticate**. Sign-in is gated by the Entra enterprise
application **Zendesk MCP**: only users whose **department group** is assigned to it can sign in.
Which departments are assigned is a decision still to be made (`entra_gate1_group_object_ids` in
`infra/terraform/entra.tf`).

> **Direct assignment only.** The app requires assignment (`appRoleAssignmentRequired = true`),
> and Entra honours only **direct** members of an assigned group for sign-in — nested groups do not
> count. The tier, by contrast, *is* evaluated transitively. So a team needs both: its department
> group **directly assigned** to the Zendesk MCP app, **and** that department nested in an
> `sg-zendesk-*` group (or the people added to one). This is the same rule vitally-mcp works by, and
> getting it wrong fails at that department's next sign-in with `AADSTS50105`.

## Getting access

**As a user:** ask the **IT & Security team** for the tier you need (most people need read). Once
granted, it applies within about a minute. You then connect your Zendesk account as above.

**As an admin:** two Entra steps, both required.

1. **Sign-in (Gate 1):** Entra → Enterprise applications → **Zendesk MCP** → Users and groups →
   assign the department group. (Terraform: add it to `entra_gate1_group_object_ids`.)
2. **Tier (Gate 2):** add the user, or nest their department, in `sg-zendesk-readers`, `-editors` or
   `-admins`.

Revoking follows vitally-mcp's procedure exactly, with the `sg-zendesk-*` groups. It matters whether
the person holds the tier **directly** or **through a department**: removing an inheriting user from
the tier group does nothing. Confirm with the transitive query, which is the same lookup the server
makes:

```bash
export MSYS_NO_PATHCONV=1
TIER=<sg-zendesk-readers|editors|admins object id>; SUBJECT=<their-entra-object-id>
az rest --method get --url "https://graph.microsoft.com/v1.0/groups/$TIER/transitiveMembers?\$count=true&\$filter=id eq '$SUBJECT'" --headers "ConsistencyLevel=eventual" --query "length(value)" -o tsv
```

`0` for every tier group is the only answer that means revoked.

Changes take effect within `Authorization:LiveGroupCacheSeconds` (60 s) while Microsoft Graph is
reachable. During a Graph outage each person's last known-good tier is served for up to
`Authorization:LiveGroupStaleSeconds` (1 hour) before calls are denied.

### Urgent revocation

Do all of these, in this order:

1. **Remove the tier** (as above). This normally stops their use of this server within about 60 seconds.
2. **Suspend the person in Zendesk, or downgrade their role.** This invalidates their Zendesk token
   at Zendesk. It is the one step that stops Zendesk calls even during a Graph outage, because every
   call this server makes on their behalf uses that token.
3. **Disable the Entra account and revoke their sessions.** This stops new sign-ins and refreshes.
4. Optionally, remove their stored token: the Key Vault secret `zendesk-user-<object id>` (or
   `zendesk-staging-user-…`). Write a new version whose value is `{"revoked":true}` rather than
   deleting it — see the Key Vault section in `CLAUDE.md`.

An Entra access token they already hold stays valid until it expires (about 60–90 minutes). Step 2
is what makes that harmless for Zendesk. If you need to stop all writes for everyone at once, set
`Authorization__ReadOnly=true` on the Container App. If Graph is down as well, disable ingress
instead — vitally-mcp's ACCESS.md explains why `ReadOnly` misbehaves during a Graph outage.

## How it's set up (in brief)

- **Sign-in:** Microsoft Entra, via the server's OAuth façade, as in vitally-mcp.
- **Authorisation:** your `zendesk:*` tier is resolved from your **live** Entra group membership on
  each call (transitively, so nested department groups count).
- **Zendesk access:** your own Zendesk OAuth grant, stored as one Key Vault secret readable only by
  the server's managed identity, refreshed automatically, and revoked when you disconnect. There is
  no shared Zendesk account.
- **Auditing:** every tool call is recorded against your Entra object id, with the tool, its
  arguments (which can include names or emails you searched for), the ids of the records touched,
  the tier used, the outcome and a correlation id. Records go to Application Insights `AppEvents`.
  Zendesk response bodies are never recorded. Zendesk's own audit log separately shows every change
  under your agent's name.
- **Hosting:** Azure Container Apps + Azure Key Vault, proposed hostname `zendesk-mcp.fiscaltec.com`.

Group membership is managed in Entra by the IT & Security team. Questions: contact the Infrastructure team.

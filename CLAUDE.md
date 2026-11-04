# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Model Context Protocol (MCP) server in C# giving FISCAL staff access to Zendesk Support and the Help Center. It is built to the standard of [fiscaltec/vitally-mcp](https://github.com/fiscaltec/vitally-mcp) — the same host, OAuth façade, RBAC, audit, telemetry, CI/CD and Azure shape, ported rather than reinvented — and reproduces the functionality of Swifteq's hosted Zendesk MCP server.

**Status: built and deployed; validation partly done.** The Azure and Entra estate was built on 2026-10-28/29 with `az` CLI by `docs/runbooks/build-the-estate.md` (#3; the runbook fixes it needed are in PR #16), as the Vitally estate is, and serves at `https://zendesk-mcp.fiscaltec.com` and `https://zendesk-mcp-staging.fiscaltec.com` (image `sha-d5528c7`). The secret-expiry scanner Job (9.4) followed the same day; it posts to the Teams channel Vitally's scanner uses, its card titled "Zendesk MCP:". Phase 14 passed: on both origins the owner signed in with Microsoft, connected their own Zendesk agent account, called `List_brands`, and the `ZendeskToolCall` audit records reached `AppEvents`. `infra/terraform/` describes the same estate as a record and is **never applied** (it passes `terraform validate`). The Zendesk endpoints were written from the API documentation; on 2026-10-28 every **read** tool was run against the live account with a development token in read-only mode, and what that showed was fixed (PR #1). The same day every write tool was exercised against throwaway records (all since deleted): test tickets, an inactive personal macro, draft articles, a test category and section, and a test end user and organisations; every feature-switch refusal was confirmed. That found and fixed `Update_ticket` silently dropping tag changes, `Add_user_to_organization` moving people out of their organisation without `replace_existing`, and a rename reporting the old name. A public reply on an existing ticket was sent too, notifying only the test agent. On 2026-11-04 staging was given its own **read-only** Zendesk OAuth client (`fiscal_zendesk_mcp_staging`, allowed and requested scope `read`), and production's client trimmed to `read write` with only its own callback; staging reconnected through it live. The same day the rest of per-user OAuth was exercised live and checked against the server's own logs (#4): refresh past expiry, persisted to Key Vault and surviving a deploy; disconnect, tombstone and reconnect; and a refused login (an end user) revoked; the contents of a tool-call record and its upstream record were confirmed too (#5). Not yet exercised: the rest of #5 (multi-record searches, denial records, the negative control), the email-mismatch refusal (no spare agent login; accepted, as it shares the refuse-and-revoke code), reads as an ordinary agent rather than an admin (#6), and eight shapes the account could not show, among them a reused idempotency key with a different body (#7), plus #26's `has_value` on non-drop-down custom fields and its messaging channels (no messaging tickets). Read the open issues rather than trusting this paragraph: it describes 2026-11-04. Code marks each remaining uncertainty `// Unverified against the live API` — writes checked against the documentation only say "docs-checked, not live-tested". See *Staging validation* before trusting any tool against production.

**Provenance.** Issue references written `vitally-mcp#NNN` point at fiscaltec/vitally-mcp, where the lesson was learnt; the design and the code that carries it came across together. vitally-mcp's own `CLAUDE.md` holds the full history — dates, measurements, incident write-ups — behind the rules stated here more briefly. When this file and that one disagree about a shared component, check the code here.

**Keeping in step with vitally-mcp.** The port of everything outside the source and test folders (workflows, scripts, templates, runbooks, `CLAUDE.md` sections) is current to vitally-mcp **`0c6d4e1`** (2026-10-26), audited on 2026-11-04 in #10, whose decision table records what was ported, adapted or judged not applicable, and why. Findings that flow the other way go to #17. To check for drift, diff from that commit rather than re-auditing:

```bash
# One command: a failed or skipped fetch leaves a stale origin/main, which prints nothing with exit 0 — not "no drift".
git -C <vitally-mcp clone> fetch origin main && git -C <vitally-mcp clone> log --oneline 0c6d4e1..origin/main -- . ':!VitallyMcp' ':!VitallyMcp.Tests'
```

That lists vitally-mcp's non-source changes since this audit; decide each as #10 did, then move the commit named here. Run on 2026-11-04, it listed nothing: the one later commit, `fdc6f5e` (#197), touches only the two `.csproj` files. ⚠️ The clone must hold history back to `0c6d4e1`: in a `--depth 1` clone the command fails with `fatal: bad revision` (exit 128), which is not "no drift". Check the exit status, not just the empty output. Source drift (a fix to a shared component such as the OAuth proxy, `ToolAuthorizer` or the Graph resolver) is out of this command's scope: list it with the same range over `-- VitallyMcp`, and read each commit, since the code here was ported and renamed rather than copied file for file.

**Key characteristics:**
- 48 tools over tickets, KPI/SLA/CSAT reporting, macros, users and organisations, configuration and the Help Center — see *Tools*.
- **Per-user Zendesk OAuth.** Every Zendesk call is made with the signed-in person's own Zendesk token. There is no shared Zendesk credential in a deployed target, so Zendesk attributes every change to the agent and applies their role, group and brand restrictions. This is the main architectural difference from vitally-mcp, which calls Vitally with one shared key.
- **Two OAuth relationships, never mixed:** Entra signs people in to *this server* (`/oauth/*`, unchanged from vitally-mcp); Zendesk OAuth lets the server act *as them* (`/zendesk/connect`, `/zendesk/callback`).
- **Streamable HTTP transport** (MCP 2026-07-28), stateless, on `ModelContextProtocol.AspNetCore`.
- RBAC tiers from live Entra group membership (`zendesk:read` / `zendesk:write` / `zendesk:delete`), enforced at `ZendeskService.SendAsync` and used to filter `tools/list` per caller.
- Per-user audit trail to Application Insights `AppEvents`, reads included.
- .NET 10 ASP.NET Core; `ModelContextProtocol` SDK — check `ZendeskMcp.csproj` for the version rather than this line.

## Common Development Commands

### Build, test, run

```powershell
# Restore + build + run the test suite
dotnet test --solution ZendeskMcp.sln -c Debug

# Run a single test class
dotnet test --solution ZendeskMcp.sln -c Debug --filter-class "*TicketsToolsTests"

# Start the server in dev mode: no Entra, no Key Vault, every call made with ONE Zendesk OAuth access token
$env:OAuth__NoAuth = "true"
$env:ASPNETCORE_ENVIRONMENT = "Development"   # the development token is refused in any other environment
$env:Zendesk__Subdomain = "your-subdomain"
$env:Zendesk__DevelopmentAccessToken = "<a Zendesk OAuth access token>"   # API tokens are being retired
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project ZendeskMcp/ZendeskMcp.csproj
```

`StartupGuards.EnsureDevelopmentTokenIsLocal` refuses the development token unless `OAuth:NoAuth=true`, no `Zendesk:KeyVaultUri` is set **and** the host runs in the `Development` environment (there is no `launchSettings.json`, so `dotnet run` alone starts in Production and refuses). Behind real sign-in it would turn a per-user server back into a shared service account, attributing everyone's changes to the token's owner. Do not weaken that guard.

To exercise per-user OAuth locally instead, leave `NoAuth` off, set `Zendesk:OAuthClientId` and `Zendesk:DevelopmentOAuthClientSecret` (in-memory token store; nothing survives a restart), and add `http://localhost:5099/zendesk/callback` to the redirect URLs of a **separate development** Zendesk OAuth client — not `fiscal_zendesk_mcp` (production) or `fiscal_zendesk_mcp_staging` (staging), each of which lists only its own deployed callback (`docs/runbooks/build-the-estate.md`, Phase 0.2). The tier checks stay on: `Authorization:Enabled=false` is refused without `NoAuth` (#12), so give the local host `Authorization:LiveGroupCheck=true` and the Entra tier group ids, as a deployed target has.

### Smoke-testing the server

With the server running locally (or against the deployed URL with a real JWT in the `Authorization` header):

```powershell
# OAuth metadata document — clients use this to discover the auth server
Invoke-RestMethod http://localhost:5099/.well-known/oauth-protected-resource

# MCP initialise. This deliberately requests 2025-06-18: the `initialize` handshake exists only in
# revisions up to 2025-11-25, because 2026-07-28 removed it in favour of per-request `_meta` and
# headers. So this is a legacy-path smoke test, and the server replies with a revision it supports.
$body = @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{ name='smoke'; version='0.0.1' } } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body

# tools/list on the legacy path (no MCP-Protocol-Version header — the server accepts it)
$body = @{ jsonrpc='2.0'; id=2; method='tools/list' } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body
```

**The 2026-07-28 path** needs, together: the `MCP-Protocol-Version: 2026-07-28` header;
`_meta/io.modelcontextprotocol/protocolVersion` and `_meta/io.modelcontextprotocol/clientCapabilities`
(an object) in `params`; `Mcp-Method` equal to the body's `method`; and, for `tools/call`, an
`Mcp-Name` header carrying the tool name — omit that and the call is rejected *before* the tool
runs, so nothing is audited (vitally-mcp#147). Each omission has its own error; vitally-mcp's
`CLAUDE.md` lists them.

```powershell
$meta = @{
  'io.modelcontextprotocol/protocolVersion'   = '2026-07-28'
  'io.modelcontextprotocol/clientCapabilities' = @{}
  'io.modelcontextprotocol/clientInfo'         = @{ name = 'smoke'; version = '0.0.1' }
}
$body = @{ jsonrpc='2.0'; id=3; method='tools/list'; params=@{ _meta=$meta } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' `
  -Headers @{ Accept='application/json, text/event-stream'; 'MCP-Protocol-Version'='2026-07-28'; 'Mcp-Method'='tools/list' } `
  -Body $body
```

A successful response carries `ttlMs: 300000` and `cacheScope: "private"` alongside `tools`.

## Installing for End Users

FISCAL staff point their MCP client at `https://zendesk-mcp.fiscaltec.com/mcp` (decided 2026-10-28; live since 2026-10-29, #3). They sign in with Microsoft, then connect their own Zendesk account through the one-time link the first Zendesk tool returns. [ACCESS.md](ACCESS.md) is the user-facing guide; keep it in step with any change to either sign-in.

## GitHub issues (issue-driven work)

Track non-trivial work as GitHub issues, raised with the **`/create-issue`** skill
(`.claude/skills/create-issue/SKILL.md`, ported from `searledan/spendy` in #11) and moved through their
statuses with `.claude/scripts/issue-status.sh` — both over `gh api` REST. Trivial one-off changes
don't need an issue — use judgement. This mirrors the flow used in `searledan/rosetechnologies.co.uk`,
`searledan/dansearle.co.uk` and `searledan/spendy`; the labels below were copied from vitally-mcp on
2026-10-28 (`gh label clone fiscaltec/vitally-mcp --repo fiscaltec/zendesk-mcp`), so names, colours
and descriptions match there.

⚠️ **In a Claude Code cloud session every `gh issue …` command fails** with `HTTP 403: GitHub GraphQL
is not available from Claude Code sessions` — they are GraphQL underneath, and the session's GitHub
proxy serves REST only (hit when #2–#9 were raised). The skill and the script above carry the REST
forms; to read or edit an issue otherwise, use `gh api …/issues/<n>` and `gh api -X PATCH …/issues/<n>`.
`gh pr merge` is GraphQL too, so merges use the REST endpoint instead
(the merge step below), and the merge gate reads over REST since 2026-10-28 (#2). Before that it read
through `gh pr view` and `gh api graphql`, so in a cloud session it denied every merge at its first
step (*"could not resolve the current repo (failing closed)"*, verified by feeding it a synthetic
payload for PR #1). This follows `searledan/spendy`, which made the same move in its #484.

**Why it matters here:** each issue is normally picked up in a *fresh* session, often in a worktree or
via a subagent. The issue body and this file are the only context that new session gets, so an issue
that assumes prior conversation is an issue that cannot be worked.

**Labels** — one of each per issue:

- **type** (categorises the issue — *distinct* from the Conventional-Commits type in the PR title):
  `feature`, `bug`, `tech-debt`, `security`, `ux`, `content`, `ops` (infra/deployment/config),
  `documentation`
- **priority:** `priority: high` / `priority: medium` / `priority: low`
- **status** (progresses `ready` → `in-progress` → `complete`; `blocked` is a side-state):
  `status: ready` (defined enough to start) / `status: in-progress` / `status: blocked` (waiting on a
  dependency) / `status: complete` (work merged)

**Lifecycle** — one `status:` label at a time:

1. Pick a `status: ready` issue respecting priority, or raise one with `/create-issue` (type +
   priority + `status: ready`, or `status: blocked` with a comment saying what it waits for; nobody
   assigned).
2. Flip to in-progress: `bash .claude/scripts/issue-status.sh <n> in-progress`. That also **assigns
   `searledan`**, the maintainer, who asked on 2026-11-04 to be assigned to an issue only while it is actively worked; every
   other status unassigns them (`ISSUE_ASSIGNEE` overrides the login, and `ISSUE_ASSIGNEE=` leaves
   assignees alone). The script adds the new label before removing the old, refuses a label the
   repository lacks — adding an unknown label silently *creates* it — and reads the issue back, exiting
   non-zero if it does not show the requested state. `.github/scripts/tests/issue-status.test.sh`
   runs it against a stub `gh` in CI.
3. Branch `<cc-type>/<short-description>`, where `<cc-type>` is the **Conventional-Commits** type used
   in the PR title — not the issue's type label. They map loosely: `feature` → `feat/…`,
   `documentation`/`content` → `docs/…`, `tech-debt` → `chore/…` or `refactor/…`, `bug` → `fix/…`,
   `ops` → `ci/…` or `chore/…`; validation and test-only work of either type → `test/…`.
4. Reference `#<n>` in commits where relevant.
5. Open the PR with **`Closes #<n>`**. `.github/workflows/pr-title.yml` enforces the
   Conventional-Commits prefix on the PR *title*, and `main` takes squash merges — so the PR title
   becomes the commit subject on `main`. On squash-merge the issue auto-closes; flip it to
   `status: complete` then (`bash .claude/scripts/issue-status.sh <n> complete`), so a finished issue ends up *closed + `status: complete`*, distinguishable
   from one closed as won't-fix or duplicate.
6. If work stalls, `bash .claude/scripts/issue-status.sh <n> blocked` (which unassigns the maintainer) and
   comment exactly what it is waiting for.

**What actually gates a merge** — the `Secure branches` ruleset (id `26057156`), applied here on
2026-10-28 as a field-for-field copy of vitally-mcp's (`16293585`), compared rule by rule (#2). It
covers the default branch: no deletion, no force-push, squash only, and
`required_approving_review_count` **0** — no approving review is needed. What is required is all
review threads resolved, the branch up to date with `main` (`strict_required_status_checks_policy`),
and these checks green: `Analyze (csharp) (csharp)` (CodeQL's real check name — the doubled suffix
is not a typo, and a required name that never reports blocks every merge), `Validate PR title`,
`Build and test (ubuntu-latest, net10.0)`, `nuget-vuln`, `image-cve`. Stale reviews are dismissed on
push. Read it with `gh api repos/fiscaltec/zendesk-mcp/rulesets/26057156` rather than inferring from
`mergeStateStatus`, which reports `BLOCKED` for unresolved threads and pending checks too.

**That ruleset is not the whole gate.** It says nothing about Copilot, and reading "0 approvals
required" as "nothing else to wait for" is exactly what merged vitally-mcp#117 with three unreviewed commits —
see *Copilot review & merge gate* below before merging anything (the section after next — a
pre-review section now sits between).

### Review the PR yourself before Copilot does

Run the `pr-review-toolkit` agents against the branch **before opening the PR**, or before the first
re-request if it is already open.

⚠️ **It does not shorten the review loop, and claiming otherwise was the first thing this section got
wrong.** Measured on vitally-mcp#166 (2026-09-25), all reconcilable from the API:

| | |
|---|---|
| Copilot reviews on the PR | **11** |
| Rounds before the agents ran (on `23c022c`) | 5, having produced 12 threads |
| Applied from the agents | **20**, in `e80c93c` |
| Copilot rounds *after* that | **6**, producing 6 more threads |

So six rounds followed the pre-review. What it changed was the **class** of defect left: the agents
cleared claims that had become false, wrong dates and counts, and gaps in files the diff never
touched — none of which Copilot had raised in five rounds, because it reviews the diff. Copilot then
kept finding a different class: what a check actually *establishes*, a command that could not run, an
unasserted capture. Run both because they do not overlap, not because the loop gets shorter.

Invoke them with `/pr-review-toolkit:review-pr`, or individually with the `Agent` tool passing
`subagent_type`. ⚠️ **Write the `pr-review-toolkit:` prefix.** `code-reviewer` and `code-simplifier`
exist in **two** installed plugins, and `feature-dev:code-reviewer` has **no Bash** — so it silently
cannot run `git` or `az`, which is exactly what makes the briefs below work.

| Agent (prefix `pr-review-toolkit:`) | Finds | Worth running when |
|---|---|---|
| `comment-analyzer` | claims that have become false, contradictions **across files**, commands that do not do what the prose says | any change touching prose, comments or runbooks |
| `silent-failure-hunter` | fail-open shapes, swallowed errors, "could not assess" rendered as a definite answer | any change adding a shell check, a catch block or a fallback |
| `code-reviewer` | guideline breaches, Terraform/infra correctness, instructions that fail if followed literally | most changes |

`pr-test-analyzer` and `type-design-analyzer` apply when tests or new types are involved.

⚠️ **These ship in a plugin, not in this repo** — there is no `.claude/agents/` here. A session
without that plugin cannot follow this section, and should say so rather than skipping the step
silently. The session that validated PR #1 did not have it, and said so in #9.

⚠️ **Do not skip this for a documentation-only change**, which is the opposite of what a reader
arriving from `searledan/spendy` will expect. Its *Pre-commit Checklist* skips step 1 for
"documentation-only changes" — but step 1 is **`/simplify`**, a reuse/quality/efficiency pass over
*code* that also applies fixes, and spendy's checklist never mentions `pr-review-toolkit` at all. So
there is no rule there to invert: the two are different tools, and looking for a `pr-review-toolkit`
skip rule in spendy is a dead end.

What transfers is the reasoning, and here it runs the other way. Prose in this repo is operational
instruction — commands people run and live-state claims they act on — so a documentation change is a
correctness change. vitally-mcp#165 was prose-only and still took five rounds. (vitally-mcp#166 is often described as
documentation-only and was not: it added a required Terraform variable and an `env` block, where two
of its findings landed.)

Three things determine whether the agents are worth the tokens:

- **Brief them concretely.** Name where to look, say that prose is operational instruction, and tell
  them they may run read-only `az` and `git`. The findings that mattered came from checking claims
  against git history, the live Azure estate and files *outside the diff* — none of which happens
  unprompted. ⚠️ **Confirm your Azure elevation first** (`/infra-pims`): an agent hitting a lapsed
  PIM gets `AuthorizationFailed` and, per the `silent-failure-hunter` row above, is liable to report
  that as a definite negative rather than "could not assess". See the note under
  vitally-mcp's `docs/runbooks/entra-cutover-staging-validation.md`.
- **Argue with them.** They revise: one withdrew a DRY-based recommendation once told its cited
  precedent (`verify-oauth-metadata.sh`, a CI-executed script) did not transfer to human diagnostics.
- **Expect truncated reports.** Every agent run so far has cut off mid-finding and needed a
  follow-up `SendMessage` — three of three on vitally-mcp#166, two of two on this change.

**What to do with the output.** Verify each finding against the source before acting — on vitally-mcp#166 an
agent's headline claim was right and its supporting count was not, and on this section every number
an agent challenged turned out to be wrong. Fix what survives verification; where you disagree, say
so in the PR rather than only in a reply the next session cannot see. **Stop after one pass plus one
re-run** if the first pass changed anything substantive; a third adds little and the loop has no
natural end, since agents will keep finding something.

⚠️ **It satisfies nothing in the merge gate.** Copilot still reviews, the threads still have to be
worked and resolved, and its latest review must still be on the current head. This is work done
*before* that loop, not a substitute for any step in it.

⚠️ **Run every command you put in a document, with a negative control.** Six backslash-continuation
failures in one session produced plausible text that would not run, and a fail-closed check was
written three times before it actually failed closed — proven only by pointing it at a nonexistent
resource. Reading the snippet never caught any of them; executing it caught all of them.

### Copilot review & merge gate

Copilot reviews every PR automatically, **asynchronously**, and its reviews are always `COMMENTED` —
never `APPROVED`. So it can never satisfy an approval-count rule, and the ruleset above is blind to
it. Two consequences, both of which have cost real time in this repo and its siblings:

- **`mergeStateStatus: CLEAN` does NOT mean Copilot has finished.** It reflects the ruleset only.
  `searledan/rosetechnologies.co.uk` records its own version of this: PR #24 there merged 15 seconds before
  Copilot's second review posted two valid comments. Here, **vitally-mcp#117 merged with Copilot having reviewed
  only the first pushed commit** — the two fixes Copilot itself asked for went in unseen, while
  `CLEAN` held the whole time because the threads had been resolved.
- **A push does not reliably start a re-review.** The automatic request fires when the PR is
  *opened*. After pushing review fixes you must **re-request explicitly**, or you will wait for a
  review that is never coming and read the silence as approval.

**The merge sequence — a loop, not a one-shot:**

1. Open the PR; let the checks and the first Copilot pass run.
2. Each round:
   1. **Wait for Copilot to finish reviewing the _current head_.** Done only when it is **not** in
      requested reviewers **AND** its latest review's `commit_id` **equals the head SHA**. A stale
      review of an earlier commit does not count, and timestamps cannot be lined up against the head
      commit — compare the SHA.
      ```bash
      n=<PR>; head=$(git ls-remote origin "refs/pull/$n/head" | awk '{print $1}')
      # Pending? Copilot's last request/removal/review event; review_requested = still pending.
      gh api --paginate "repos/fiscaltec/zendesk-mcp/issues/$n/timeline?per_page=100" \
        --jq '.[] | select(((.event=="review_requested" or .event=="review_request_removed") and ((.requested_reviewer.login // "") | test("^(Copilot|copilot-pull-request-reviewer(\\[bot\\])?)$"))) or (.event=="reviewed" and ((.user.login // "") | test("^(Copilot|copilot-pull-request-reviewer(\\[bot\\])?)$")))) | .event' | tail -1
      # Copilot's latest submitted review: compare its commit with $head.
      gh api --paginate "repos/fiscaltec/zendesk-mcp/pulls/$n/reviews?per_page=100" \
        --jq '.[] | select(.submitted_at != null and ((.user.login // "") | test("^(Copilot|copilot-pull-request-reviewer(\\[bot\\])?)$"))) | "\(.submitted_at) \(.commit_id)"' | sort | tail -1
      ```

      Both are REST, so they work in a Claude Code cloud session as well as locally (run against
      PR #1 on 2026-10-28, with a control showing the same filters return a review that exists).
      Pending comes from the **timeline** because `requested_reviewers` never lists Copilot
      (`searledan/spendy`'s `github.mjs` records the same).

      ⚠️ **The REST reviews collection can lag — read it anyway, for now.** On vitally-mcp#129
      (2026-09-15) `…/pulls/N/reviews` reported Copilot's latest review as `ee1770c` submitted
      `14:45:44Z` while GraphQL reported `1559962` at `18:43:14Z` — nine reviews and nearly four
      hours apart. That is why this block and the hook used GraphQL until 2026-10-28; GraphQL is
      refused in cloud sessions, so both now use REST (#2). A lagging list only ever names an
      **older** commit, so it can make you wait for a review that has already landed, never merge
      one that has not. If the head's review is visible in the GitHub UI but not here, wait and
      re-check rather than switching the gate off: a gate that cannot pass gets switched off, and
      that is worse than waiting.

      ⚠️ **Take the head SHA from `refs/pull/$n/head`, never from the PR API's head field
      (`head.sha` in REST, `headRefOid` in GraphQL), in the seconds after a push.** It lags: on vitally-mcp#122 it still reported the previous
      commit right after a push, so the comparison matched Copilot's *old* review and the gate read
      as passing. Two consequences, and the second is the expensive one: re-requesting in that
      window gets a review of the previous commit (that happened on vitally-mcp#122 too — a review arrived four
      minutes after the request, on the superseded SHA), so **wait until the API reports the new head
      before re-requesting**.

      `refs/pull/$n/head` rather than `git rev-parse HEAD`, which was what this said until the hook
      moved to the remote ref: the local checkout is only the right answer when it *is* that PR's
      branch and has nothing unpushed, and merging a second PR from another branch is normal. The
      manual check and the hook must read the same source or they disagree exactly when it matters.

      ⚠️ **"Not pending" alone is meaningless.** A request that has just been made can be followed
      by a review of the *previous* commit, and an empty requested-reviewers list says nothing at
      all about Copilot. Both conditions, always: not pending **and** the latest review on the head.
   2. A clean pass reports **`Findings: None`** in the review body and adds no threads.
      ⚠️ That string is the *current* format; the older *"reviewed N of N files … generated no new
      comments"* wording this file used to cite appears **zero times** across all 11 reviews on vitally-mcp#166,
      so do not match on it. And see the merge step below: `Findings: None` alone is not sufficient
      — twice on vitally-mcp#166 a real defect arrived on exactly that.
   3. Work every open thread: fix and reply, or reply with the reasoning — then **resolve** it.
   4. **If you pushed code in (3), re-request and go back to (1):**
      ```bash
      gh api repos/fiscaltec/zendesk-mcp/pulls/$n/requested_reviewers \
        -f 'reviewers[]=copilot-pull-request-reviewer[bot]'
      ```
      **`gh pr edit --add-reviewer` silently no-ops on the Copilot bot** — use the REST endpoint.
      `requested_reviewers` reading empty seconds later is **not** failure (see the warning above);
      check the timeline (`.event == "review_requested"`) if in doubt. Reply-only rounds need no
      re-request.

      ⚠️ **That timeline check paginates, and page one goes stale exactly when you need it.**
      `gh api …/issues/N/timeline` returns **30 events** by default, so on a PR with several review
      rounds the newest `review_requested` is not on the first page — `.[-1]` then reports an old
      timestamp and the re-request looks as though it never registered. Always `--paginate`:

      ```bash
      gh api --paginate repos/fiscaltec/zendesk-mcp/issues/$n/timeline \
        --jq '.[] | select(.event=="review_requested") | .created_at' | tail -1
      ```

      Measured on vitally-mcp#162 on 2026-09-23: the unpaginated read reported the last request at `14:58:23Z`
      while two further requests had in fact landed at `16:43:14Z` and `16:53:25Z`. The conclusion
      drawn was "the re-request is not working", which is the opposite of the truth — and the check
      this paragraph sends you to is the *authoritative* one, so getting it wrong has nowhere to fall
      back to. Note also that the API reports **UTC** while the local clock here is BST, which makes
      a fresh timestamp look an hour stale on top.
3. Only then merge (squash), re-checking all three immediately beforehand: required checks green and
   branch current, Copilot's latest review on the current head, zero unresolved threads.

   ⚠️ **Read Copilot's whole review body, never the `Findings:` count.** Twice on vitally-mcp#166 a real defect
   arrived on a pass reporting **`Findings: None`** with the gate `CLEAN` and zero threads:

   | Where it hid | What it was |
   |---|---|
   | The overview **headline** | *"Resolve the undefined `$RG` command"* — prose used `-g $RG` three lines above where `RG` is set, so the documented command errored with `expected one argument` |
   | A collapsed **"Previously missed"** section | a decision tree that chose between `AppEvents` and the console on a variable that a pre-#164 image ignores entirely |

   Both would have merged on the count alone. The headline also carries *generic* text — the same
   blurb alleged "conflicting Application Insights guidance" that did not exist — so check each claim
   rather than acting on it or dismissing it wholesale.

   **Merge through REST, pinned to the full SHA you verified** — the hook requires the pin and denies
   without it — and **write the PR number and the SHA out literally**, with nothing quoted:

   ```bash
   gh api -X PUT repos/{owner}/{repo}/pulls/129/merge -f merge_method=squash -f sha=4384e311ed03c94e79134bd7a7435b62d2e124e9
   ```

   This is the form that works everywhere: `gh pr merge` is GraphQL underneath, so it cannot run in
   a Claude Code cloud session at all. Locally it still works, and the hook still gates it (pinned
   with `--match-head-commit`), but use one form so there is one thing to get right.

   ⚠️ **Not `$n` / `$head`, and no quotes.** The hook is a `PreToolUse` hook: it sees the command
   *text*, before the shell expands anything, and it refuses any command containing quotes or shell
   metacharacters because it cannot read those with certainty. `{owner}` and `{repo}` are `gh`'s own
   placeholders, not shell syntax, so they need no quoting.

   Everything checked above is true of *one moment*; the merge runs after it, so a push landing in
   that window merges a commit nothing verified. The hook cannot see that race — it has already
   returned by then — so the `sha` field hands the check to GitHub, which refuses the merge outright
   if the head moved. Without it the gate is a snapshot, which is vitally-mcp#117's shape again.

**Mind the spellings of the bot's login — all are correct, don't "align" them.** The suffix tracks
*which API answered*, not which field you read. **REST** (`gh api …/pulls/N/reviews`,
`…/requested_reviewers`) uses `copilot-pull-request-reviewer[bot]`; **GraphQL** — and
`gh pr view --json`, which is GraphQL underneath — reports `copilot-pull-request-reviewer` without it;
timeline events can also name it `Copilot`. The commands above and the hook accept all three.

⚠️ **Never `--auto` merge a human PR.** It fires the instant CI passes and beats a pending review.
`--auto` is for Dependabot only, which carries no human review.

**This is enforced mechanically, not just documented.** `.claude/hooks/pre-merge-copilot-gate.sh` is a
`PreToolUse` hook, registered in `.claude/settings.json` under the `Bash` matcher with
`if: "Bash(*gh*)"`, that **denies** a merge (`gh pr merge`, or
`gh api` reaching `pulls/<n>/merge` with any method but GET) unless Copilot is not pending, its
latest review's `commit_id` equals the head, and unresolved threads are zero. A GraphQL merge
mutation is refused outright. It exempts Dependabot and **fails closed** — including if
`jq`/`sed`/`awk` are missing or broken, so a damaged toolchain cannot wave a merge through. Ported
from `searledan/dansearle.co.uk`, then moved to REST on 2026-10-28 (#2) after `searledan/spendy`'s
#484: it resolves the repo from the `origin` remote, reads the PR, its timeline and its reviews over
REST, and reads review threads from the cloud session's `ccr/review_threads` route when
`CLAUDE_CODE_REMOTE` is set and through GraphQL locally (REST has no thread-resolution state).
The `ccr` route takes no paging parameters and sends no `Link` header (`per_page=1`, `page=2`,
`first=2` and `limit=2` each returned all eight threads on PR #1, 2026-11-04, #12), so nothing says
whether a long PR's list is complete; the hook therefore requires every review comment REST returns
(paginated) to appear in some thread's `comment_ids`, and denies when one does not.
`.github/scripts/tests/pre-merge-copilot-gate.test.sh` runs it against stub `gh` and `git` in CI,
the allow path included; deleting either the head-review or the pin check makes it fail.

Caveats worth knowing before trusting it:

- It guards **only Claude Code's own Bash calls** — a merge from the GitHub UI is unaffected. The
  GitHub MCP connector's `merge_pull_request` and `enable_pr_auto_merge` would bypass it too, so
  `.claude/settings.json` denies both outright.
- **It resolves the head from `refs/pull/N/head` via `git ls-remote`, not from the PR API.**
  `headRefOid` lags after a push (see the warning above), and a gate that trusts it fails *open* —
  the stale value names the commit Copilot already reviewed. `ls-remote` reads the git server
  directly, so it neither lags nor cares which branch is checked out, which matters because merging
  a second PR from another branch is normal. GitHub publishes `refs/pull/N/head` on the base repo
  for **every** PR including forks, so there is no branch-name path and no local-checkout fallback:
  an earlier version had both, and the fallback compared an unrelated commit whenever a local branch
  shared a fork PR's branch name. If that ref cannot be read, the gate denies.
- A newly added hook needs `/hooks` opened once (or a restart) to activate.
- **It requires the pin** — `-f sha=<full verified sha>` on the REST form, `--match-head-commit
  <sha>` (seven characters or more) on `gh pr merge` — and denies a merge without one, given twice,
  or naming a different commit. That is not belt-and-braces: every other check here describes the
  moment the hook ran, and the merge happens afterwards.
- **Run the merge as a standalone command, starting with `gh`, on one line — no pipes, no `;`, no
  `&&`, no quotes, no `#`, no braces other than `{owner}`/`{repo}`.** The hook refuses any command
  naming `gh` and `merge` that contains those, because it cannot read it with certainty.
- **The filter is `Bash(*gh*)` because `if` matches a prefix.** Claude Code compares `if` with the
  start of each subcommand, after stripping leading `VAR=value` assignments and splitting `&&`/`;`/`|`
  chains. Reproduced on 2026-11-04 (#12) with harmless `gh pr merge --help` calls: under the old
  `Bash(gh pr merge*)` and `Bash(gh api*)` filters, `GH_HOST=github.com gh …` and `cd . && gh …`
  reached the hook, but `command gh …`, `env gh …` and `/usr/local/bin/gh …` ran without it. Under
  `Bash(*gh*)` all three reached it — the change to `if` took effect without a restart. The hook's
  own step 1 then decides, coarsely on purpose: a command is a **candidate** when `gh` and `merge`
  both appear as words anywhere in it, in any case, after quotes and backslashes are removed. A
  candidate must be written plainly — no quotes, `#`, line breaks or tabs, braces other than
  `{owner}`/`{repo}`, commas or shell metacharacters — and be `gh pr merge …` or
  `gh api … pulls/<n>/merge …` from its first word; anything else is denied, not parsed. Spotting
  bypass spellings one at a time lost repeatedly on #46 (`/usr/local/bin/gh`, `merge>log`, a `#`
  comment carrying another PR's pin, a newline joining two commands, `${IFS}` and brace expansion,
  `gh pr -R x merge`), which is why. So a path to gh, `env gh`, `command gh` or `GH_HOST=… gh` is
  refused for a merge, and any Bash call whose text names `gh` and `merge` as words — a heredoc, a
  commit message, `grep -n gh.*merge` — is denied. For anything that is not a
  merge it allows at once, without a network call; but because every such call now runs `jq`,
  `sed` and `awk`, a broken one of those denies every Bash call mentioning `gh`, not only merges.
  Testing the hook or editing text that quotes a merge command means doing it from a script file
  rather than inline. Over-matching is the safe direction; don't loosen it. The `if` filter itself
  is covered only by the live check above, not by the CI test, which calls the hook directly. It
  guards against a mistaken merge, not a determined one: a command that never spells both words (a
  shell variable, `xargs gh <file`, a `gh` alias, `curl` to the API) is invisible to it.
- **The REST reviews list can lag** (above). The lag only makes the gate deny a merge it would allow;
  wait and re-check.

Verified on porting (2026-08-28) by running the hook against real PRs: it allows vitally-mcp#118, whose Copilot
review was on the merged head, and **denies vitally-mcp#117** — *"Copilot's latest review (6808805…) is not on
the current head (58afb2b…)"* — which is the mistake that prompted the port.

**Forms** — `.github/ISSUE_TEMPLATE/` provides seven, one per type label except `ux`: `bug.yml`,
`feature.yml`, `tech-debt.yml`, `security.yml`, `ops.yml`, `documentation.yml`, `content.yml`.
Filenames match the label they preset, and the shared four match the sibling repos. Each form
presets its type label plus `status: ready`, and its title with the matching Conventional-Commits
prefix; add a `priority:` label after creating one through the web form (`/create-issue` sets it
at creation). Blank issues stay enabled for quick notes and for
`ux`. Bug and Feature carry server-specific fields (MCP client, server URL, a failure
timestamp for correlating with Application Insights) — keep those if you edit the forms.

`documentation` and `content` both map to `docs/…` branches but cover different audiences:
**Documentation** is prose for humans (`CLAUDE.md`, `README.md`, `ACCESS.md`, `docs/`), whereas **Content** is the
copy an *LLM* reads — tool `[Description]` and `Title` values and `ZendeskServerInstructions.Text`.
Content wording changes model behaviour (which tool gets picked, how it's called), so they are
defects rather than cosmetics; the form asks for the observed effect to keep that distinction sharp.

**Writing issues** — aim for "detailed enough to implement without further context". `/create-issue`
walks through these, takes each type's sections from its form, and adds a duplicate check and a
read-back. **Never put ticket, user or organisation content in an issue** — it is customer data;
describe shapes, not contents.

- **Title** — conveys the scope at a glance without reading the body.
- **Lead paragraph** — what and why, plus how it was discovered if that matters.
- **`## Problem`** (or `## Description` + `## Current state / problem`) — evidence, not assertion.
  Quote real error output, cite file paths and line numbers, and say what was *verified* versus
  *assumed*.
- **`## Proposed fix` / `## Proposed solution`** — concrete numbered steps. Record rejected
  alternatives and why, so the next session doesn't re-litigate them.
- **`## Files to create/modify`** — explicit paths.
- **`## CLAUDE.md updates needed`** — which sections of this file the change invalidates. Easy to
  forget and the most common source of drift.
- **`## Dependencies`** — related issues as `#N` with the relationship ("blocked on vitally-mcp#92", "related to
  vitally-mcp#90"); if it depends on unresolved work, use `status: blocked`.
- Prefer tables for structured data. Flag anything designed-but-unvalidated as such, explicitly.

## Architecture

### Hosting and wiring (Program.cs)

ASP.NET Core 10, `WebApplication.CreateBuilder`. Everything vitally-mcp's `Program.cs` does is here unchanged in behaviour, with the same comments: options bound and validated at startup (`Zendesk:`, `OAuth:`, `Authorization:`, `Audit:`, `ToolsListCache:`); `DefaultAzureCredential` shared by Key Vault and Graph; the Application Insights exporter and its audit routing, sampling (`EnableTraceBasedLogsSampler = false`, 10% traces) and console suppression; the Graph group resolver; JwtBearer with the single-challenge 401 and the type-only authentication-failure log; the policy handler and `AddAuthorizationFilters()` registered **unconditionally**; the call-tool filters (error surfacing, then the audit record); forwarded headers; the RFC 9728 / RFC 8414 documents and the `/oauth/*` façade.

Added for Zendesk:

| Registration | Why |
|---|---|
| `IZendeskTokenStore` → `KeyVaultZendeskTokenStore` when `Zendesk:KeyVaultUri` is set, else `InMemoryZendeskTokenStore` | one secret per connected person |
| `ZendeskOAuthClient` (singleton, named `HttpClient` `zendesk-oauth`) | authorise URL, code/refresh exchange, identity check, revoke |
| `ZendeskConnectLinks`, `ZendeskUploadSlots` (singletons over `IDistributedCache`) | one-shot capabilities for the browser and curl halves |
| `ZendeskCredentialProvider` (scoped) | the caller's own token for this request |
| `AddDistributedMemoryCache()` | backs the two above — **in-memory, so single replica** (below) |
| `app.MapZendeskConnect()`, `app.MapZendeskUploads()` | `/zendesk/connect`, `/zendesk/callback`, `PUT /uploads/{slot}` — mapped before `UseAuthentication` and not behind the bearer requirement, because a browser or curl has no Entra token; each authenticates with a capability issued to a signed-in tool caller |

⚠️ **Single replica is a correctness constraint, not a cost choice.** The `/oauth/*` proxy state (inherited from vitally-mcp), connect links, connect attempts, upload slots and `Bulk_get_ticket_conversations`'s two-calls-at-once gate (`TicketConversationsTools.Gate`, which is per account only because there is one process) all live in process memory, and a token removed on one replica stays cached on another for up to `Zendesk:TokenCacheDuration`. `containerapps.tf` pins `max_replicas = 1`. Scaling out means registering a shared `IDistributedCache` (Redis) first and moving the OAuth proxy's `IMemoryCache` state onto it — a registration change for the Zendesk parts, a small code change for the proxy.

### The Entra façade, RBAC, logging and audit — as vitally-mcp

These components are ported without behavioural change, and their rules hold here exactly. vitally-mcp's `CLAUDE.md` carries the evidence for each; the short form:

- **The OAuth proxy is a complete authorisation-server façade.** `issuer` is our origin (RFC 8414 §3.3) and equals `authorization_servers` byte for byte; `/oauth/callback` strips any upstream `iss` and appends ours (RFC 9207); `authorization_response_iss_parameter_supported: true` is honest only because of that. Change none of the three alone (vitally-mcp#90).
- **`redirect_uri` validation is the only thing between the proxy and an open redirector** — loopback on any port plus `OAuth:AllowedClientRedirectUris`. Never widen it.
- **RFC 8707 `resource` is validated, then terminated.** `OAuth:UpstreamResourceScope` set (Entra) drops a matching `resource` and names the API by scope on both `/oauth/authorize` and `/oauth/token` — on a refresh that is what keeps the token's audience right. Relaying `resource` to Entra fails with AADSTS9010010 whatever its spelling (vitally-mcp#105, vitally-mcp#108).
- **`Audience` (no trailing slash, the App ID URI) and `Resource` (with the slash, what Claude Code normalises to) differ by one character on purpose.** Do not reconcile them.
- **Upstream endpoints come from OIDC discovery**, checked against `Authority` (§4.3), cached 12 h with last-known-good fallback; startup refuses to boot without them (vitally-mcp#104).
- **`/oauth/token` services only `authorization_code` and `refresh_token`** — it injects the confidential secret, so any other grant would mint tokens without a sign-in.
- **Entitlement comes from live Entra group membership via Graph** (`Authorization:LiveGroupCheck`, transitive, keyed on `oid` — never `sub`), fresh → stale (up to `LiveGroupStaleSeconds`) → deny. No claim authorises while the live check is on (vitally-mcp#106, vitally-mcp#108, vitally-mcp#156).
- **`ZendeskService.SendAsync` is the enforcement point**; `[Authorize]` discovery filtering shares `HasEffectivePermissionAsync` with it so they cannot disagree. `Authorization:ReadOnly` denies every non-GET for everyone; `docs/runbooks/read-only-and-rbac.md` says what it is and is not for, and how to verify staging is serving it.
- **Log levels are a security control and live in `Program.cs`**, not `appsettings.json` (excluded from the image) or environment variables (lost on a recreate). Failure records log the exception *type*, the status and the resource *type* — never a message, an attached exception, a body or the rest of the path (vitally-mcp#94).
- **Four audit record shapes** — tool call (primary: caller `oid`, tool, arguments, record ids touched, tier, staleness, outcome, correlation id), action (per upstream call), service denial, tier denial. Tool arguments are recorded; upstream response bodies never are. Records route to `AppEvents` when `ApplicationInsights__ConnectionString` is set, and must not depend on trace sampling (vitally-mcp#147, vitally-mcp#178).

What changed in porting them: the policy names (`zendesk:*`), the default custom-permissions claim, the Graph tier groups (`sg-zendesk-*`), the meter and counter names (`ZendeskMcp`, `zendesk.*`), and `AuditRecordIds` (below). `IncludeReads` matters more here than in vitally-mcp: Zendesk's own audit log records changes, not reads, so this trail is the only record of who read which ticket.

### Metrics and tracing (as vitally-mcp#94, phase 6)

These are exported to Application Insights only when `ApplicationInsights__ConnectionString` is set,
like the audit records. Without it, the upstream duration goes to stdout with the rest of its audit
record.

| Signal | Name | What it answers |
|---|---|---|
| Upstream call duration | `durationMs` on the `ZendeskUpstreamCall` record (`AuditDurationMs` in `AppEvents`) | Which of a composite tool's upstream calls was slow, joined to the `ZendeskToolCall` record by correlation id. `SendAsync` times from send to body read, including any 429 waits. `DownloadAsync` times each hop to its response headers only, so an attachment's body is not in it |
| 429 retries / exhaustion | `zendesk.ratelimit.retries`, `zendesk.ratelimit.exhausted` | How often the server exceeds the **account's** Zendesk budget, which every other integration shares |
| Pager truncations | `zendesk.autopager.truncations`, tag `resource` | Whether `Zendesk:MaxAutoPageFetches` is set where people hit it |
| Cache hit/miss | `zendesk.cache.lookups`, tags `cache` = `user_token` / `group_membership` / `oidc_discovery`, `result` = `hit` / `miss` | Per *lookup*. A miss means the cache did not answer, so an external lookup was needed. It is counted before the call, so a cancelled request is still a miss, and so is a stale Graph serve after a failed call |
| MCP spans | activity source `Experimental.ModelContextProtocol` | A span per request, tool calls included. Sampled, and sanitised (below) |

Three caveats on reading them, each of which would otherwise send a query wrong (the code cites them
here, in `ZendeskMetrics`):

- **Calls that fail before a response have no duration.** A timeout, a DNS failure or a refused
  connection never reaches `LogAction`, so there is no `ZendeskUpstreamCall` row. (A non-2xx
  response is different: `SendAsync` logs it, with its duration, before throwing.) Timeouts are the slowest calls of
  all; look for them in the failure records (`Bulk_get_ticket_conversations` reports a timed-out
  ticket in its result, and logs it as `Tool call partly failed` with the correlation id). The clock also starts *after* the caller's credential
  is resolved, so a cold Key Vault read of the person's token, or a token refresh, shows in the
  tool-call duration and in no upstream one. The same goes for recovery after a `401`: the rejected
  call and its retry each get a row, and the `OnTokenRejectedAsync` refresh between them is in
  neither. `Audit:Enabled` and `Audit:IncludeReads` gate it too,
  so turning reads off to cut ingest cost removes every GET's duration as well. `AuditDurationMs`
  is the same property name on `ZendeskToolCall`, so filter on `Name`. Values are truncated to whole
  milliseconds: `0` is a real measurement, and only `-1` means unmeasured.
- **The `group_membership` hit rate counts lookups, and is inflated by design.** `ToolAuthorizer`
  does not memoise, and each `tools/call` runs several checks: vitally-mcp counted at least three on
  SDK 2.2.0, and that has not been re-counted on 2.3.0 here. A `tools/list` checks every tool. Read
  the rate as "how often Graph was spared", never as "how well `LiveGroupCacheSeconds` is tuned".
  `user_token` is inflated the same way: every upstream call resolves the caller's credential, and
  the provider reads the store again near expiry and after a `401`, so a composite tool records one
  lookup per upstream call. It is counted only by the Key Vault store, where a cached "not connected"
  is a hit. Read it as "how often Key Vault was spared", not as tuning for `TokenCacheDuration`.
- **The `oidc_discovery` hit rate looks healthy during a provider outage.** After a failed refresh,
  the last-known-good copy is re-cached for a minute (`FailedRefreshRetryInterval`), and reads in
  that minute are hits.

The counters live on one `Meter`, `ZendeskMcp` (`ZendeskMetrics`, created through `IMeterFactory`).
Every consumer takes it as an **optional** constructor parameter, so a registration that never
resolved would record nothing while every unit test passed.
`TheComposedHost_InjectsTheCounters_IntoTheComponentsThatRecordThem` proves the real wiring for
`ZendeskService` (a truncation) and the Graph resolver. The OIDC resolver, the Key Vault token store
and the rate limiter are not reachable from that harness, so only their unit tests cover them. Names
live in `TelemetrySources`, which `Program.cs` and the tests share.

⚠️ **The SDK's activity source is `Experimental.ModelContextProtocol`, not the bare
`ModelContextProtocol`.** A source registered under a name nothing publishes is accepted silently and
captures nothing. `TheMcpSdk_PublishesTheSourceWeRegister_AndNoSanitisedSpanCarriesCallerText` fails
if it stops being published, so an SDK bump that drops the prefix shows up as a red test.

⚠️ **The SDK's meter (same name) is deliberately NOT registered.** Its
`mcp.server.operation.duration` histogram has `gen_ai.tool.name` as a dimension. A `tools/call`
naming a tool that does not exist puts the invented name there: caller text, unbounded cardinality,
and every one recorded, because metrics are never sampled. A span processor cannot rewrite metric
tags. Per-tool latency is already queryable, unsampled, from `AuditDurationMs` on `ZendeskToolCall`.
The exporter test pins the meter as unregistered. If per-method failure rates are ever wanted,
register it behind a view that drops `gen_ai.tool.name`. Never register it as-is.

⚠️ **Metric tags carry only values this code fixes.** A caller-controlled tag value is a cardinality
explosion, and it puts customer text in a metrics store. The pager's `resource` tag comes from
`ZendeskService.ResourceTypeOf`: the first segment after `/api/v2/`, checked against the
`KnownResourceTypes` allowlist that the failure log uses. So the tag is `/api/v2/tickets` or
`/api/v2/tickets/…`, never a ticket id, and anything else reads `/api/v2/(unrecognised)`. Every Help
Center path collapses to `/api/v2/help_center…`, so the tag cannot tell articles from sections.

**Sampling: 10% head sampling of traces.** It needs `SamplingRatio = 0.1F` **and**
`TracesPerSecond = null`, because `TracesPerSecond` (default 5) overrides the ratio whenever it is
set. It is safe only because `EnableTraceBasedLogsSampler = false` (vitally-mcp#178): logs, the audit
records among them, are exported whatever the trace sampling, and metrics are never sampled. When
querying:

- **Count the sampled tables with `sum(ItemCount)`, not `count()`.** Each `AppRequests` and
  `AppDependencies` row stands for about ten. `AppEvents` and `AppTraces` are logs and stay exact, so
  `count()` is right there.
- **"Errors at 100%" is not available.** Head sampling decides before the outcome is known. Failures
  stay visible, unsampled, through the failure records. The counters do not cover them: the only
  failure they count is rate-limit exhaustion.

**Spans are sanitised before export by `SpanSanitisingProcessor`**, because they go to
`AppRequests` / `AppDependencies`. Those tables have their own retention, and tool arguments may not
go there. They share a workspace with the audit records. Table-level RBAC can grant a narrow reader one
table, but Azure RBAC only ever grants: it cannot remove `AppEvents` from anyone holding inherited
workspace-wide read, which is how most readers get access. So in practice it does not keep the tables
apart. vitally-mcp reviewed who holds that read on its workspace (vitally-mcp#146); this workspace has
not been reviewed the same way. The MCP branch works from an **allowlist**, not a list of known leaks. vitally-mcp's probes
of the SDK found caller text reaching spans by seven routes:

| Where | What the SDK does | The processor |
|---|---|---|
| `StatusDescription`, any span | Copies a failed tool call's whole error text there: the caller's input and Zendesk's response body | Clears it. The status code stays |
| `mcp.method.name`, MCP spans | Records whatever method the caller named | Keeps it only if the SDK defines it (`RequestMethods` / `NotificationMethods`); otherwise `unrecognised` |
| `gen_ai.tool.name`, MCP spans | Records an invented tool name verbatim | Keeps it only if it is in `KnownToolNames`; otherwise `unrecognised` |
| Every other MCP tag | `jsonrpc.request.id`, `mcp.resource.uri` and `gen_ai.prompt.name` carry caller values | Dropped unless on `AllowedMcpTags`, so a future SDK tag is dropped by default |
| The span name, MCP spans | `<method>` or `<method> <target>`, where the target is the caller's tool, prompt or resource | **Rebuilt** from the cleaned method and tool, never patched |

`QueryStringRedactingProcessor` replaces the query string on `HttpClient` dependency spans with `?*`.
The search, count and analytics tools send free text as `?query=` (for example `Search_tickets`,
`Search_users`, `Search_organizations`), often a customer's name or email.
The .NET runtime already redacts it the same way, but a process-wide switch turns that off; the
processor keeps the guarantee in this repository.

⚠️ **Paths are not redacted.** `AppDependencies` still carries caller-typed **path segments**: ids,
locales and custom-object keys. That gap is open. The PII test
(`TheMcpSdk_PublishesTheSourceWeRegister_AndNoSanitisedSpanCarriesCallerText`) covers MCP SDK and
ASP.NET Core server spans only, because its harness stubs the primary HTTP handler and so creates no
dependency span. `jsonrpc.request.id` is covered by a unit test only, because the harness sends a
constant id.

**Processor order is pinned.** OpenTelemetry runs processors in the order they were added. The
exporter test asserts that both processors run before the distro's Live Metrics and batch exporter.

⚠️ **The exporter test disposes its `TracerProvider` and `MeterProvider` explicitly** (`LoggingFilterTests`). Inside the
suite, disposing the `WebApplicationFactory` did not dispose it. A leaked provider stayed subscribed
to the MCP source and sanitised later tests' spans. A post-dispose guard catches a recurrence, and
`TelemetryRedactionTests` is in the serialised collection so that guard cannot flake. vitally-mcp
did not establish the cause, so do not generalise from it.

### Per-user Zendesk OAuth

```
tool call ──> ZendeskService ──> ZendeskCredentialProvider ──(no token)──> ZendeskConnectionRequiredException
                                                                              carries /zendesk/connect?ticket=…
person's browser ──> /zendesk/connect ──302──> {sub}.zendesk.com/oauth/authorizations/new (PKCE S256, state)
                                                         │ sets HttpOnly SameSite=Lax cookie = state
Zendesk ──302──> /zendesk/callback?code&state ──> exchange code ──> GET /api/v2/users/me with the new token
                                                         ├─ agent/admin AND email == Entra sign-in name → store
                                                         └─ otherwise → revoke at Zendesk, refuse
```

| Component | Rule |
|---|---|
| `ZendeskConnectLinks` | A link is 256 random bits naming the Entra `oid` and sign-in name it was issued to, valid `ConnectLinkLifetime` (15 min), spent only on a successful store. Issued **only** inside an authenticated caller's own tool result. |
| `/zendesk/connect` | Peeks the link (an abandoned Zendesk sign-in can be retried), starts an attempt with a fresh PKCE verifier, pins `state` to the browser with a cookie. |
| `/zendesk/callback` | Requires cookie == `state` (fixed-time compare) — a code completed in another browser is login CSRF. Spends the attempt. Exchanges the code with the client secret **and** the verifier. Reads `/api/v2/users/me` with the new token and refuses unless the role is agent/admin and, with `RequireEmailMatch` (default on), the email equals the sign-in name case-insensitively. A refused token is revoked immediately. A reconnect revokes the grant it replaces with `RevokeGrantAsync`, which refreshes an expired grant first: revoking an expired access token answers 401, read as revoked, while its refresh token stays live (#12). Result pages HTML-encode everything Zendesk returned. |
| Sign-in name | `CallerIdentity.TryGetSignInName` prefers `upn` (an optional claim `entra.tf` requests) over `preferred_username`, which Microsoft documents as mutable. |
| Startup guards | `RequireEmailMatch=false` is refused unless `OAuth:NoAuth`; `Authorization:Enabled=false` (no tier checks at all) is refused unless NoAuth, so local per-user OAuth runs with the tier checks on (#12); the development token is refused unless NoAuth, no Key Vault **and** the Development environment; connect links and the callback refuse to derive their origin from the request unless NoAuth — behind sign-in `OAuth:PublicBaseUrl` is required. |
| Why the email match matters | Without it, anyone who opens a person's link — forwarded, leaked, or planted in a ticket — attaches **their** Zendesk account to that person's identity, and every later call that person makes acts as the opener. The cookie stops a planted *code*; the email match stops a planted *link*. Keep both. |
| `ZendeskCredentialProvider` | Reads the caller's `oid` (no `oid` → refused); no token → connect link; token within 2 min of expiry → refresh **under a per-person lock** (Zendesk rotates the refresh token, so a second concurrent refresh would present a spent token, fail `invalid_grant`, and disconnect someone who did nothing wrong); refresh refused with `invalid_grant`/`invalid_token`/`unauthorized_client` → forget the token and hand back a link. |
| `ZendeskService` on a `401` | `OnTokenRejectedAsync(rejectedHeader)` decides, **under the person's lock and about the token that was actually sent**: if the stored token has since changed (a refresh or reconnect landed while the request was in flight), retry with it; else, if a refresh token exists, refresh and retry once; only then revoke the grant at Zendesk, forget it, and fail with a fresh connect link — revoked first because a 401 straight after a refresh may be specific to one endpoint, leaving the fresh grant live in Key Vault history otherwise (#12). Forgetting "the current token" on any 401 would wipe a working grant that replaced the rejected one. |
| `ZendeskConnectionRequiredException` | Surfaced verbatim by `ToolErrorResult` — its message *is* the link. `ZendeskServerInstructions` tells the model to hand it over unchanged. |
| Refresh persistence | The store caches **before** it writes to Key Vault, and a failed write after a refresh is logged, not thrown: Zendesk has already spent the old refresh token, so losing the new one would disconnect the person. The connect callback stores under the person's lock, so an in-flight refresh cannot overwrite a fresh connection. |
| `KeyVaultZendeskTokenStore` | One secret per person, `{TokenSecretPrefix}{oid}` (`zendesk-user-` in production, `zendesk-staging-user-` in staging, so the targets never share a token), JSON, tagged `kind=zendesk-user-token`, `ExpiresOn` = refresh expiry. Reads cached `TokenCacheDuration`, including "not connected". A value that is neither a grant nor a tombstone is **not** "not connected" — the grant in it may be live — so it is logged (with the secret version), never cached, and thrown as `ZendeskStoredTokenUnreadableException` carrying whatever tokens can be read; `ZendeskCredentialProvider` revokes those under the person's lock and tombstones it, and a reconnect revokes them (#12). That happens on whichever call meets the value first, so `Get_zendesk_connection`, a read-tier tool, can revoke and tombstone, outside `Authorization:ReadOnly`'s verb check as `Disconnect_zendesk` is. Every discard path — disconnect, reconnect, a final 401, an unreadable value — revokes through `RevokeGrantAsync`, and logs *"may still be live at Zendesk"* when it cannot confirm it. **Removal writes a tombstone version, never a delete**: soft delete is on, and a soft-deleted name cannot be set again until purged, so a delete would make reconnecting fail for the whole retention window. Older versions keep the old token, which is why disconnecting revokes at Zendesk first. |
| Clients and scopes | **One Zendesk OAuth client per target** (split 2026-11-04): production `fiscal_zendesk_mcp` requests `read write`; staging `fiscal_zendesk_mcp_staging` requests `read` (`Zendesk__OAuthScopes`) with its own secret (`Zendesk__OAuthClientSecretRef` = `zendesk-staging-oauth-client-secret`). Each client's **allowed scopes** in Admin Centre equal what its target requests, so a staging token cannot write to Zendesk even with `Authorization__ReadOnly` lifted, and each lists only its own callback. A `read`-only token **can** revoke itself (`DELETE /api/v2/oauth/tokens/current`): confirmed live on staging 2026-11-04 16:39Z — `Disconnect_zendesk` returned `disconnected`, Key Vault wrote the tombstone, and no revocation warning was logged — so the disconnect and refused-login revocations work on staging's client too. See `docs/runbooks/entra-and-zendesk-oauth.md`. |
| Lifetimes | access tokens requested at 2 h and refresh tokens at 90 days (`AccessTokenExpiresInSeconds` null requests Zendesk's legacy non-expiring token instead). |
| Managed identity | Needs **Key Vault Secrets Officer** on the vault (it writes), which also lets it overwrite `zendesk-oauth-client-secret`. Accepted: the identity already holds every person's token; see `infra/terraform/identity.tf`. |

### ZendeskService — the one path to Zendesk

`AddHttpClient<ZendeskService>()` behind `ZendeskRateLimitHandler`. Every call:

1. **RBAC backstop** — `ToolAuthorizer.EnsureAuthorizedAsync(verb)`: GET → read, POST/PUT/PATCH → write, DELETE → delete. Tools keep that mapping faithful: reads (macro preview included) are GETs, and **a write tool never issues a DELETE** — `Add_user_to_organization` moves a user with a `PUT`, for instance — so the write tier never needs the delete tier.
2. **Credential** — the caller's own `Authorization` header from `ZendeskCredentialProvider`, per request; the shared `HttpClient` is never mutated.
3. **Audit** — `LogAction` per upstream call; record ids captured from the **raw** body by `AuditRecordIds` before any tool reshapes it, or, when the body names none, from the request URL or a bulk update's request body (below).
4. **Errors** — a non-2xx becomes `HttpRequestException` carrying Zendesk's body (truncated to 1 KB), which `ToolErrorResult` surfaces: `{"error":"RecordInvalid","details":…}` is what the model needs to correct its call. The server-side record logs status and resource type only.

**Credential boundaries.** The base URL decides where the caller's token goes, and tool arguments can come from a model that has just read untrusted ticket content:

- **Only `*.zendesk.com` hosts ever receive the credential.** `ResolveBrandBaseUrlAsync(brand_url)` accepts only `https` hosts named by the account's own `/api/v2/brands` (cached 10 min), and translates a brand's **host mapping** to that brand's `{subdomain}.zendesk.com` host — a mapping can name any domain, and a stale or malicious one would otherwise receive every reader's token. A Help Center tool passes `brand_url` through the `brandUrl` parameter — never builds a host itself.
- `DownloadAsync(url)` (attachments, embedded article images) applies the same host rule, refuses `/api/` paths (an image `src` must not make each reader's token call the API), and follows redirects itself — the `ZendeskService` handler is built with `AllowAutoRedirect = false` — at most three, https only, to an account host (credential re-sent) or `*.zdusercontent.com` (no credential; signed URLs). Anything else is refused.
- Inlined article images are capped at 2 MB each and 8 MB per call, and only in the main body — a base64 copy per translation per image could otherwise cost gigabytes on the single replica.
- `ZendeskService.Segment()` escapes caller strings used in paths; `BuildUrl` escapes every query value.

**`AuditRecordIds` for Zendesk shapes:** ids are numbers (rendered as raw text); the primary collection is the first array of objects in the body (`tickets`, `results`, `articles` — Zendesk serialises it before sideloads such as `users`), `meta`/`links` ignored; a single record is bare or wrapped (`{"ticket":{…}}`). The mutation-URL fallback accepts only a **numeric** last segment — Zendesk paths also end in locales and collection names, and recording `en-gb` as a record id would be a fabricated identifier. A bulk update answers with a background job rather than the records, so for `PUT …/update_many` the last fallback reads the ids from the request body's `{"tickets":[{"id":…}]}` (all whole numbers or nothing; `ZendeskService` passes the body's JSON to it). Those are the tickets the write was *sent* for, not proof each changed. A `job_status` is never read as a record: its own id is a job token, so only a finished job's `results[].id` count.

**Bulk updates.** `Bulk_update_tickets` follows Swifteq's `bulk_update_tickets` schema (read 2026-11-04): `ticket_ids` with `changes`, or `tickets` with a change per entry (`changes` then adds to each; a field in both is an error). It takes no comment, CC or attachment: those stay with `Update_ticket`. Without `confirmation` it previews: one `show_many` read, each ticket as `will_change` (`from`/`to` per field), `unchanged` or `cannot_change` (missing, hidden, or closed with anything but tags), and a `confirmation`. That is an HMAC, under a key held only in process memory, over the caller, the normalised batch (order-free: tickets, fields and tags sorted), `solve_incidents` and every ticket's `updated_at`, valid 30 minutes. A write without it, with a different batch, or after any ticket changed is refused; a restart means previewing again. The write is one `PUT /api/v2/tickets/update_many` with `{"tickets":[…]}`, each entry carrying only what differs plus `safe_update`/`updated_stamp`, and tags as the whole new list from that read (the per-entry form's handling of `additional_tags` is undocumented, and the single-ticket PUT ignores it). It then polls `job_statuses/{id}` every `JobStatusPollInterval` up to `MaxJobStatusPolls` times; a failure while polling is reported as `unknown`, never thrown, and the caller's cancellation no longer applies, since the write has gone. A ticket counts as `changed` only when the job reports success **and** a fresh `show_many` shows the change, otherwise `failed` or `not_confirmed`, and `undo` gives the previous values in the `tickets` shape (the custom status where the ticket had one). What it cannot restore is warned about and left out: a return to New, an unset priority, incidents a solved problem solved, a problem link a type change dropped; an entry with nothing left is omitted, because passed back it would be refused. Solving a problem ticket that has unsolved linked incidents needs `solve_incidents`, as in Swifteq and `Update_ticket`; each problem ticket being solved costs one paged read of its incidents.

**Paging.** Tools that page for the caller pass Zendesk's cursor through (`after_cursor` → `page[after]`, `pagination_info {has_more, next_cursor, page_size}`). `GetAllPagesAsync` pages on the server's behalf (sideloads merged) up to `MaxAutoPageFetches`, marking the audit record truncated when it stops early. A page envelope it cannot read (not an object, or a `meta` of the wrong shape) throws `ZendeskResponseFormatException` rather than returning a silently short result; an optional `beforeNextPage` callback runs before each later page and may throw to stop paging (`Bulk_get_ticket_conversations` stops at its deadline that way, never cancelling a request in flight). Analytics tools read at most `MaxAnalyticsTickets` tickets and refuse beyond it, saying how to split the range.

**Rate limits.** Zendesk's budget is per *account* (200–2,500 requests a minute by plan) and shared with every other integration. The handler retries `429` on `Retry-After` and warns as `X-Rate-Limit-Remaining` runs low; analytics tools bound their own concurrency.

### Uploads (`ZendeskUploads.cs`)

`Request_ticket_attachment_upload` issues a slot: 256 random bits, one use, 15 minutes, an exact byte count, the caller's identity and permission claims, and at most five open per person. The PUT reads into a buffer that grows with what arrives (never the declared size up front). The client then `PUT`s the bytes (curl) to `/uploads/{slot}`; the server sets `HttpContext.User` from the slot **before** resolving scoped services, so the write tier is re-checked (live Graph included) and the upload is audited and made as that person, then streams the bytes to `POST /api/v2/uploads` and returns the `upload_token`. Nothing is stored. Help Center images take the other route: Zendesk's pre-signed URL, which the client uploads to directly and to which the server never sends a credential.

### Account-wide feature switches

`Zendesk:Features` — `AllowPublicReplies`, `AllowArticlePublishing`, `AllowHelpCenterStructureChanges`, `AllowMacroChanges`, `AllowUserAndOrganizationChanges`, all **off** by default in the application and all **on** in FISCAL's deployment (Terraform defaults, decided 2026-10-28) — guard the actions whose effect reaches beyond the agent: customers' inboxes (public comments, CCs, public macro replies and public merge comments), the public Help Center (published articles and who can view an article, and categories and sections, which have no draft state), every agent's macros, who can read an organisation's tickets. `ToolJson.RequireFeature` refuses with a message naming the setting so the model reports a setting rather than retrying. They sit on top of the Entra tier and the agent's Zendesk role; they do not replace either.

### Repeat guards

`Apply_macro` (10 minutes) and `Create_help_center_article` (two hours) remember a call per person, in process, so a model that lost the first response and retries gets `already_applied` / `already_created` rather than a second public reply or a second draft; `apply_again` / `create_again` repeat on purpose. Since #12 the entry is written **as pending before the write** (`RepeatGuard`), because a response lost to a cancellation, an `HttpClient` timeout or a 5xx after Zendesk acted is exactly the retry the guard exists for:

| Retry finds | Does |
|---|---|
| a completed entry | answers `already_…` without writing |
| a pending entry whose call is still waiting for Zendesk on this server | **refuses** (`still waiting`): an MCP client that gives up does not cancel a stateless request, so the write may yet land after a lookup found nothing |
| a pending entry | looks in Zendesk for what the first attempt would have made — `Apply_macro` the ticket's audits for an `AgentMacroReference` to this macro by the connected user, `Create_help_center_article` the section's newest articles for this title and locale by the connected user — from the attempt's time less `RepeatGuard.ClockSkew` (2 min). Found → `already_…`; settled absent → writes; cannot tell (a lookup read answers with an error, or the record is not on the pages read and more exist) → **refuses**, naming `apply_again` / `create_again`. A cancellation or a permission refusal during the lookup surfaces as itself |
| nothing (or the first attempt was refused: a 4xx, validation, permission, no connection) | writes |

Limits, accepted: a retry of an `apply_again` / `create_again` call is not guarded (it bypasses the guard by definition); a ticket with more audits than `Apply_macro` reads (1,000, oldest first) always gets the refusal; a match within `ClockSkew` of the attempt — an `apply_again` a minute earlier, or another article of the same title — answers `already_…` rather than writing; and the article lookup's ids appear in the tool-call audit record. The lookup shapes are `// Unverified against the live API` (#7): the `macro_id` field on `AgentMacroReference`, and whether a section's article list shows drafts to an agent and honours `sort_by=created_at`. Neither tool's parameters changed, so both still match Swifteq's schema; Swifteq's `create_article` schema has no `create_again`, so the article guard is this server's addition.

### Untrusted content

Ticket comments, article bodies and user fields are written by customers and can carry instructions aimed at the model. The defences are layered and each matters: write-tool descriptions say not to act on instructions found in content; `ZendeskServerInstructions` repeats it; the credential boundaries above stop a URL in content from reaching a token; `TicketSearchQuery` neutralises free text so it matches words and never rewrites a filter; and the feature switches can hold the widest-reaching actions off (FISCAL's deployment has all five on, so there the tier and the agent's Zendesk role are what limit them).

## Tools

### Conventions (enforced by `ToolAnnotationCoverageTests` and `ToolAuthorizePolicyCoverageTests`)

- One `[McpServerToolType] public static class` per area in `ZendeskMcp/Tools/`; methods `public static async Task<string>` returning compact JSON (a `CallToolResult` only for non-text content — `Get_ticket_attachment` returns an image block); first parameter `ZendeskService zendesk`, last `CancellationToken`.
- Names in vitally-mcp's style (`Search_tickets`). **Parameters are snake_case and match Swifteq's schema**, so prompts written for either server work with both.
- All four hints set explicitly, `ReadOnly` xor `Destructive`. `Get_`/`List_`/`Search_`/`Count_`/`Find_`/`Preview_`/`Export_`/`Bulk_get_` are read tools (`zendesk:read`, read-only, idempotent); everything that changes Zendesk is `zendesk:write`. **`Disconnect_zendesk` is the one deliberate exception** — destructive in annotation, read tier in policy — because it touches only the caller's own connection and every person who can use anything must be able to manage that. For the same reason `ReadOnlyToolFilter` keeps it in read-only mode, the emergency lock-down, when cutting one's own connection matters most; its revoke goes through `ZendeskOAuthClient`, so the read-only verb check never applied to it (found 2026-11-04, when staging offered no way to disconnect).
- Validate arguments and throw `ArgumentException` naming the parameter — the message reaches the model.
- Shared helpers: `ToolJson` (query pairs, picking fields, pagination block, page-size and id validation, feature refusal), `TicketSearchQuery` (the ticket filters shared by Search/Count/metrics/SLA tools; `channel` reuses `TicketCountHelpers.Channels`, so a channel filter selects exactly its `group_by channel` group, and `custom_fields` is checked against the account's field definitions before any search — tools build the query with `TicketSearchHelpers.BuildQueryAsync`, not `TicketSearchQuery.Build`), `TicketSla` (the `sla` object shared by Get_ticket, Search_tickets, Get_sla_queue and Get_sla_summary).

### Catalogue

| Area | File | Tools |
|---|---|---|
| Connection | `ConnectionTools.cs` | `Get_zendesk_connection`, `Get_zendesk_connect_link`, `Disconnect_zendesk` |
| Tickets | `TicketsTools.cs`, `TicketsBulkTools.cs`, `TicketMergeTools.cs`, `TicketConversationsTools.cs`, `TicketAttachmentsTools.cs` | `Search_tickets`, `Count_tickets`, `Get_ticket`, `Get_ticket_conversation`, `Bulk_get_ticket_conversations`, `Create_ticket`, `Update_ticket`, `Bulk_update_tickets`, `Merge_tickets`, `Get_ticket_attachment`, `Request_ticket_attachment_upload` |
| Reporting | `AnalyticsTools.cs`, `SatisfactionTools.cs`, `TicketSla.cs` | `Get_ticket_metrics_summary`, `Get_sla_summary`, `Get_sla_queue`, `Get_satisfaction_ratings` |
| Macros | `MacrosTools.cs` | `Search_macros`, `Preview_macro`, `Apply_macro`, `Create_macro`, `Update_macro`, `Set_macros_active` |
| People | `UsersTools.cs`, `OrganizationsTools.cs` | `Search_users`, `Find_agent_by_name`, `Search_organizations`, `Add_user_to_organization` |
| Configuration | `ConfigurationTools.cs` | `List_ticket_fields`, `List_ticket_forms`, `List_triggers`, `List_brands` |
| Help Center | `HelpCenterArticlesTools.cs`, `HelpCenterStructureTools.cs`, `HelpCenterMediaTools.cs` | `Search_help_center_articles`, `Get_help_center_article`, `Create_help_center_article`, `Update_article_translation`, `Set_help_center_article_visibility`, `Export_help_center_articles`, `List_help_center_categories`, `Create_help_center_category`, `Update_help_center_category`, `List_help_center_sections`, `Create_help_center_section`, `Update_help_center_section`, `List_user_segments`, `List_permission_groups`, `Request_help_center_image_upload`, `Create_help_center_image` |

Dropped from Swifteq's set: `get_usage` and `submit_mcp_feedback` (Swifteq billing and support).

### Adding a tool

1. Put it in the area's class (or a new `XxxTools.cs`), following the conventions above.
2. Route every Zendesk call through `ZendeskService` — never another `HttpClient` — and keep the verb faithful to the tier.
3. Caller-supplied `brand_url` goes through the `brandUrl` parameter; caller strings in paths through `ZendeskService.Segment`.
4. A write tool: add the untrusted-content sentence to its description, and a feature switch if its effect reaches beyond the agent.
5. If it reads or writes something whose response shape `AuditRecordIds` would not find ids in, add a test there.
6. Add tests in `ZendeskMcp.Tests/Tools/` using `TestHelpers.CreateFakeZendesk` (routed and recording) and `TestHelpers.BuildZendeskService`.
7. Update the catalogue here and in `README.md`, and `ZendeskServerInstructions` if it changes how the model should choose tools.

## Important Notes

- **UK English** in code comments and documentation.
- **Never call Zendesk around `ZendeskService`**, never send a credential to a host it has not validated, and never add a shared-credential fallback to `ZendeskCredentialProvider`.
- **Error handling:** `HttpRequestException` (Zendesk's body), `UnauthorizedAccessException` (read-only, RBAC, feature switch), `ArgumentException` (validation) and `ZendeskConnectionRequiredException` (the link) are surfaced to the client; anything else gets the SDK's generic message and is logged.
- **`tools/list` cache hints** (`ToolsListCache:`) stay `Private` while discovery is filtered per caller.
- **Configuration:** never hard-code credentials. Deployed targets use Key Vault via the managed identity; local development uses the development token (NoAuth only) or the development OAuth client secret.

## Testing

xUnit v3 on **Microsoft.Testing.Platform** (not VSTest): `global.json` selects the runner, the test project is an `Exe`, and VSTest-only flags (`--filter "FullyQualifiedName~…"`, `--logger`, `--collect`) are rejected — use `--filter-class` / `--filter-method`, `--report-trx`, `--coverage`.

The tests are immune to a shell set up to run the server locally: `AmbientConfiguration`, a module initialiser in the test assembly, clears any inherited variable under the sections `Program.cs` reads (`OAuth__`, `Authorization__`, `Zendesk__`, `Audit__`, `ToolsListCache__`, `ApplicationInsights__`, `Logging__`), plus `ASPNETCORE_ENVIRONMENT`, `AllowedHosts` and `OTEL_*`, before any test runs, so the variables used to run the server locally cannot change the results (#8). It changes only the test process's environment; fixtures set their own values afterwards.

| Area | Tests |
|---|---|
| Per-user OAuth | `ZendeskCredentialProviderTests` (own bearer, connect link with no request made, 401 → forget + link, refresh and rotation stored, one refresh under concurrency, refused refresh, development token, a final 401 revokes before forgetting, an unreadable grant revoked and tombstoned), `ZendeskConnectEndpointsTests` (full flow through the host, wrong email / end user refused **and revoked**, missing cookie, one-shot link and state, encoded result page, reconnect over an expired or unreadable grant), `ZendeskTokenStoreTests` (secret shape, absence cached, tombstone not delete, unreadable refused and never cached), `ZendeskOAuthClientRevokeGrantTests` (refresh before revoking, a 401 not taken as proof while a refresh token lives), `AuthorizationGuardStartupTests` |
| Transport | `ZendeskServiceTests` (escaping, error bodies, cursor paging and cap, brand and download host boundaries, RBAC backstop), `AuditRecordIdsTests`, `ZendeskRateLimitHandlerTests` |
| Uploads | `ZendeskUploadsTests` (through the host: exact size, one use, inline, identity and claims carried, tier re-checked) |
| Shared components | everything ported from vitally-mcp — OAuth proxy, discovery, RBAC, Graph resolver, stale entitlement, audit composition, logging filters, telemetry redaction, metrics |
| Tools | `Tools/*ToolsTests` — request built (verb, path, query, body), result shaped, validation, feature switches, credential boundaries |

## Deployment

The same shape as vitally-mcp — Azure Container Apps (production warm, staging scale-to-zero) in a VNet, private Key Vault, the Vitally estate's private ACR **shared** (`vitallyproducruksouth`, repository `zendesk-mcp`; `infra/terraform/acr.tf` sets out the cross-estate write access that costs), Application Insights behind AMPLS, GitHub Actions deploying through OIDC federated credentials, a staging gate and a release train — built with `az` CLI by `docs/runbooks/build-the-estate.md`, and described in `infra/terraform/`, which is a record kept in step by hand and **never applied**, exactly as vitally-mcp keeps its own (decided 2026-10-28; its README holds the recorded decisions and what is still open). `.github/workflows/` deploys onto the built estate and never invokes Terraform. Built on 2026-10-28/29 (#3), Phase 14 included — the runbook fixes that took are in PR #16; the scanner Job (9.4) followed the same day.

⚠️ **Change the live estate with `az`, then update the Terraform to match** — in the same PR. Do not run `terraform apply` against it: with no state, a plan reads every resource as a create, and applying would collide with what exists.

### GitHub environments and the one deploy identity

`production` and `staging` each have one protection rule: a **deployment branch policy allowing `main` only** (applied 2026-10-29, #3; Phase 13 of the build runbook sets it and its Verify fails on anything else). No required reviewers, no wait timer, on either. A branch policy checks the ref of the *workflow run*, not deploy.yml's `ref` input, so the release train (scheduled and dispatched on `main`) is unaffected; what it stops is a run from any other branch obtaining either environment's Azure credential.

**One managed identity serves both targets — an accepted risk, carried over from vitally-mcp.** `zendesk-prod-id-uksouth` has a federated credential for each environment and `Contributor` on **both** Container Apps, so a token minted for `environment:staging` **can roll production**. The per-app role assignments are not a boundary: one identity holds both. vitally-mcp accepted this on 2026-08-28 (its `CLAUDE.md`, *One managed identity serves both targets*), and it holds here on the same basis: **both environments carry exactly the same protection**, so anyone who can deploy to staging could already deploy to production, and the shared identity grants nothing extra. Don't "fix" it on sight.

**What would change the answer:** a protection rule on `production` that `staging` lacks — required reviewers, a wait timer, a narrower branch policy. The shared identity is then a way round that gate. That happened briefly on 2026-10-29: a production-only reviewer was added and removed the same day for this reason. If production needs a human gate, first split the identity — a `zendesk-staging-id-uksouth` holding every role `identity.tf` gives the shared one, scoped to staging where the scope allows: `Key Vault Secrets Officer`, `AcrPull`, the custom *Zendesk MCP ACR importer* role (deploy.yml's `az acr import`), `Monitoring Metrics Publisher` (without it staging's audit records are dropped silently), `ContainerApp Reader` on the resource group (the staging gate), Graph `GroupMember.Read.All` (admin consent) and `Contributor` on the staging app only — with the staging federated credential and the staging app's identity moved to it; then add the reviewer. Record that in `identity.tf` and the runbook (Phases 4, 9, 10).

These settings cannot be read or changed from a Claude Code cloud session: its GitHub proxy refuses the environments, variables and secrets APIs. Use `gh` from Cloud Shell or WSL. #17 tracks which of the build's findings vitally-mcp should take.

### Staging rides the release train

Each nightly tag is deployed to staging first, then to production with the **same image digest**,
and a failed staging smoke stops production (the design is vitally-mcp#171's; `release.yml` and
`staging-gate.yml`). So staging is normally on the same tag as production. In two cases it is not, and
both are normal, so check its image before concluding anything from how it behaves:

| Case | What the train does | How you know |
|---|---|---|
| **Pinned**: `STAGING_PINNED` on the `staging` GitHub environment is non-empty | Skips staging and ships production. The value is the reason, printed as a notice on every run that cuts a tag while it is set | `gh variable list --env staging` |
| **Absent**: `check-serving-revisions.sh` exit 3, because `az` says the app does not exist | Skips staging and ships production | the gate job's `Staging absent` notice. ⚠️ A wrong `CONTAINER_APP` on the `staging` environment (it must be `zendesk-staging-ca-uksouth`) reads the same way if no app has that name. If it names another **existing** app (both apps are in `zendesk-prod-rg-uksouth`), the gate says deploy and the train's staging job rolls **that** app |

Anything else the gate cannot establish, a wrong `RESOURCE_GROUP` included, fails the run rather
than skipping. Pin staging whenever it
is running something ahead of production, such as a branch mid-validation, which the next nightly tag
would otherwise overwrite:

```bash
gh variable set STAGING_PINNED --env staging --body "validating <branch> for #<n>"   # pin
gh variable delete STAGING_PINNED --env staging                                     # unpin
```

⚠️ **Unpinning does not restore staging.** Tracking resumes from the **next** tag, which in a quiet week
can be days away. To catch it up now, dispatch the current tag to staging **with its digest** (from the
production job's summary in the last train run), so nothing is rebuilt over the tag production runs:

```bash
gh workflow run deploy.yml -f target=staging -f ref=<tag> -f image_tag=<tag> -f image_digest=sha256:<…>
```

These are `gh` calls on GitHub's environments and Actions APIs, so run them from Cloud Shell or WSL.
A Claude Code cloud session's proxy refuses the environments and variables APIs (see *GitHub
environments* above), and these commands have not been run from this repository. vitally-mcp runs
the same ones against its identically shaped gate.

### The deploy smoke covers the OAuth metadata, not just liveness

`deploy.yml` runs `.github/scripts/verify-oauth-metadata.sh <origin>` after the revision is live and
before the rollback step, so a failure reverts the revision. `/health` == 200 and unauthenticated
`/mcp` == 401 both stay green while the metadata documents are wrong. A wrong `issuer`, a broken
`issuer` ↔ `authorization_servers` pairing, a bad `jwks_uri`, an `authorization_endpoint` that is not
this origin's `/oauth/authorize`, a dropped `iss` flag, a null-serialised optional in the
protected-resource documents, or (added here, not in vitally-mcp) a `resource` that names another
target would each break every MCP client at its next sign-in **and
deploy green** (vitally-mcp#110).

Run it by hand against any origin; that is why it is a script and not inline YAML:

```bash
bash .github/scripts/verify-oauth-metadata.sh https://zendesk-mcp.fiscaltec.com
bash .github/scripts/verify-oauth-metadata.sh https://zendesk-mcp-staging.fiscaltec.com
bash .github/scripts/verify-oauth-metadata.sh http://localhost:5099   # a local run
```

Run on 2026-11-04: **13 assertions pass** on both deployed origins. As a negative control,
`https://ZENDESK-MCP.fiscaltec.com` reaches the same server but **fails with 6 problems** and exit 1,
because nothing is normalised.

Things about it that are deliberate, and should not be "tidied":

- **The whole assertion set retries, not just the fetches.** Container Apps reports a new revision
  Healthy *before* ingress finishes shifting traffic. So a check that runs at once can be answered by
  the **old** revision, with a valid document carrying the previous configuration. In vitally-mcp,
  asserting once turned that race into the rollback of a healthy deploy. Failures are emitted only
  after the final attempt.
- **No normalisation anywhere.** Trailing slashes and case are compared literally, because that is
  what clients do.
- **A cold staging start prints alarming lines, not usually a failure.** Staging scales to zero and
  the script retries each fetch only twice, so a cold start can print `200 but the body is not valid
  JSON` fetch lines. The whole set retries six times (`ATTEMPTS`), so it fails only if the cold start
  outlasts every attempt. CI warms the replica with the `/health` smoke first; by hand, run
  `curl <origin>/health` before reading anything into a failure.
- **It is invoked through `bash`**, not by its executable bit. An edit on Windows with
  `core.filemode=false` can silently drop the mode.

### Verifying an identity change without a browser

Re-run these after any change to the Entra app, the `OAuth__*` settings or the proxy. Together they
cover every failure mode except "a real person cannot sign in", which needs a person (#4's runbook).
Steps 3–7 are `curl` against the public origin, so they run anywhere, a cloud session included. Steps
1 and 3–7 were run against staging on 2026-11-04, with the negative controls shown.

1. `bash .github/scripts/verify-oauth-metadata.sh <origin>` (above).
2. The new revision **starting at all** proves `StartupGuards` fetched Entra's discovery document and
   matched its `issuer` to `OAuth:Authority`. A malformed or unreachable authority is a boot failure,
   not a sign-in failure. It does not prove the authority names the **right** tenant: a valid
   authority for another tenant boots happily. Step 4 checks that. ⚠️ The public origin cannot show this: if the new revision fails to boot, the previous one
   may still be serving, and every curl here passes. Check the revision itself
   (`az containerapp revision list -n <app> -g zendesk-prod-rg-uksouth --query "[].{name:name, health:properties.healthState, traffic:properties.trafficWeight}"`).
3. `POST /oauth/register` (any `client_name`, a loopback `redirect_uris`) returns a `client_id`. This
   is the shared Entra appId, by design: every client gets the same one.
4. `GET /oauth/authorize?…` answers **302** given every parameter: `response_type=code`, the
   `client_id` from step 3, a loopback `redirect_uri`, a `state` (without one it is **400** `Missing
   redirect_uri or state`), a real 43-character S256 `code_challenge` with
   `code_challenge_method=S256` (Entra rejects a short one with `AADSTS501491` at step 5), a `scope`,
   and `resource=<origin>/`. Assert on the `Location`: it starts
   `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/` (FISCAL's tenant,
   `tenant_id` in `infra/terraform/variables.tf`), **`resource` is absent**, `scope` carries
   `https://zendesk-mcp.fiscaltec.com/mcp.access` after the client's own scopes, and `redirect_uri` is
   the origin's own `/oauth/callback`. The scope names production's App ID URI on **both** targets,
   because one app registration serves both.
5. **Follow that `Location` to Entra** and read the **body**: `urlLogin` means every parameter was
   accepted, and an `AADSTS` code means one was not. ⚠️ Entra answers **HTTP 200 either way**, so the
   status code proves nothing. Negative control: the same URL with `client_id` set to all zeros
   returned 200 carrying `AADSTS700038`.
6. `/oauth/authorize` with a `resource` this origin does not publish (production's, asked of staging)
   → **400 `invalid_target`**. With the published one → 302 (step 4). Terminating the parameter must
   not have become ignoring it. A `redirect_uri` that is not loopback or allow-listed → **400**
   `redirect_uri is not allowed`.
7. `POST /mcp` unauthenticated → **401** with `WWW-Authenticate: Bearer resource_metadata="<origin>/.well-known/oauth-protected-resource/mcp"`.
   With a junk bearer → 401 plus `error="invalid_token"`.

What is left for a person, because it needs a real token: sign-in per tier, `tools/list` as a
**department-nested** user, a reader denied a write tool, and decoding the token for `aud`, `iss`,
`oid` and `upn`. `docs/runbooks/zendesk-oauth-staging-validation.md` (#4) covers sign-in and the
read tier (its step 3); the department-nested user, a reader denied a write tool and the token
decode are not written up there yet.

#### If an identity change fails

Fix it forwards, in the Entra app registration, the Gate 1 assignments or the Container App
configuration. There is no other provider to revert to.

⚠️ **The failure that hides from every automated check is a client-secret mismatch.** It applies to
**both** OAuth clients here:

- **Entra:** the app boots, `/health` is 200, unauthenticated `/mcp` is 401, and every check above
  passes, because none of them presents the secret. It surfaces only at `/oauth/token`, as
  `invalid_client`, for everyone at once. Both targets send the Container App secret
  `entra-oauth-client-secret`, a **copy** of Key Vault's `entra-mcp-client-secret`, so changing the
  vault alone changes nothing the app sends (vitally-mcp#138). And `az containerapp secret set` does
  not roll a revision: production's warm replica keeps the old value until restarted, which
  staging's cold starts hide.
- **Zendesk:** existing connections keep working until their access token is within two minutes of
  expiry (`ZendeskCredentialProvider`'s refresh skew), because the secret is used only to connect and
  to refresh. Revoking (`Disconnect_zendesk`, a refused login) sends only the access token, so it
  still works with a wrong secret, unless the token has to be refreshed first. Then each refresh fails, by Zendesk's OAuth
  documentation with `invalid_client` (docs-checked, not live-tested).
  That is deliberately not among the errors that forget a grant (`ZendeskCredentialProvider`), so
  the call fails with the SDK's generic error, nobody is disconnected, and fixing the secret recovers
  everyone, once the app's cached copy expires (`TokenCacheDuration`, 5 minutes) or the revision
  restarts. New connections fail at once. Each target has its own client and vault secret since #27.
  See `docs/runbooks/entra-and-zendesk-oauth.md`, *Rotate a client secret*.

Verify a secret change by signing in and connecting, never by a health probe.

### Freezing deploys

**`gh workflow disable release.yml`** (re-enable with `gh workflow enable release.yml`). That stops
tagging, releasing and deploying together — the coherent unit: a freeze must not leave a GitHub
Release behind for something that never shipped. Nothing is lost by pausing; the next run cuts one
tag and generates notes from everything since the previous one. **Do not add an `AUTO_DEPLOY`-style
variable** to keep tagging during a freeze — vitally-mcp tried it (vitally-mcp#111), it produced a
Release deployed nowhere, and it was reverted (vitally-mcp#115).

An attended deploy stays available through `deploy.yml`'s own `workflow_dispatch`, staging first:

```bash
gh workflow run deploy.yml -f target=staging -f ref=<tag> -f image_tag=<tag>
# read the digest from that run's summary, then:
gh workflow run deploy.yml -f target=production -f ref=<tag> -f image_tag=<tag> -f image_digest=sha256:<…>
```

⚠️ A production dispatch without `image_digest` rebuilds and force-imports over the tag, so staging can
cold-start into an image nobody smoke-tested. To recover a failed train run, use
`gh run rerun <id> --failed` — not a fresh `gh workflow run release.yml`, which finds no new tag and is
a green no-op. Train-driven deploys run inside `release.yml`'s run, so they never appear in
`gh run list --workflow=deploy.yml`.

### Staging validation

Steps 1, 4, 5 and 6 are written out, with the log queries that verify them, in `docs/runbooks/zendesk-oauth-staging-validation.md`. Before production, on staging against the real Zendesk account, with `Authorization__ReadOnly=true`. **Since 2026-11-04 staging's Zendesk OAuth client allows only `read`**, so write checks cannot run through staging at all — not even with the guard lifted: they run through production, on throwaway records, with the care below.

1. Sign in (Entra), then connect a Zendesk agent; confirm a mismatched Zendesk login is refused and its token revoked.
2. Run every read tool once, and every write tool against a test ticket, test macro (inactive) and draft article, with the feature switches as production will have them.
3. Check each place the code says `// Unverified against the live API` — the response shapes in particular — and remove the marker once confirmed. The read-side markers were settled live on 2026-10-28; what remains is write endpoints (docs-checked only) and shapes the validation account could not show (no messaging tickets, checkbox fields, custom objects, macro attachments or next-reply SLA targets; one Help Center locale; agent and account in the same time zone; and #26's `custom_fields` `has_value` on anything but a drop-down — confirmed on a drop-down, with repeated `via:` terms ORing and two custom fields ANDing, through Swifteq's server on 2026-11-04).
4. Confirm refresh: leave a connection past its access-token expiry and call again.
5. Confirm the audit records in `AppEvents` (`AppRoleName` separates staging) carry the expected ids.
6. Disconnect, and confirm the token is revoked in Zendesk and the secret reads as a tombstone.

Where each step stands, mapped to its issue (updated 2026-11-04):

| Step | State | Issue |
|---|---|---|
| Staging itself | **built** 2026-10-28/29, read-only, at `https://zendesk-mcp-staging.fiscaltec.com` | #3 |
| 1 — sign-in, connect, mismatched login refused and revoked | **sign-in and connect done** on both origins (2026-10-29, Phase 14); **an end-user login refused and revoked** on staging (2026-11-04); the email-mismatch branch not exercised live (no spare agent login; accepted); the standing runbook is `docs/runbooks/zendesk-oauth-staging-validation.md` (every step and log check, as run on 2026-11-04) | #4 |
| 2 — every read and write tool | **done live** with a development token (PR #1), as an **admin**; repeating the reads as an ordinary agent is open | #6 |
| 3 — `Unverified` markers | read-side settled; eight remain that need data the account lacks, plus #26's `has_value` on non-drop-down fields and messaging channels | #7, #26 |
| 4 — refresh | **done** on production 2026-11-04: a six-day-old grant refreshed, the rotated token persisted (Key Vault `SecretSet`), across the `v1.1.1` deploy | #4 |
| 5 — audit records in `AppEvents` | **partly done** 2026-11-04 on production: a tool-call record carries the caller `oid`, tool, arguments, record id, tier, client and outcome, and its correlation id joins the upstream record; multi-record searches, the denial records and the negative control are open | #5 |
| 6 — disconnect, revoke, tombstone | **done** on production 2026-11-04 (staging could not, until `ReadOnlyToolFilter` kept `Disconnect_zendesk`): refresh, revoke, tombstone `SecretSet`, no delete, then reconnect | #4 |

⚠️ **There is one Zendesk account and no sandbox**, as vitally-mcp has one Vitally tenant: a write
made for testing reaches real customers. Staging can no longer write (its client is `read`-only), so
write checks happen through production on throwaway records. If staging's client is ever widened for a
write test, lift `Authorization__ReadOnly` only for that test, with vitally-mcp's two independent
restores (a shell trap plus a detached failsafe that reads the guard back off the revision actually
serving traffic). Creating a ticket also fires the *Notify all agents of
received request* trigger, which emails every agent. PR #1's write tests paused that trigger for the
seconds around each create; #4 records how staging should handle it.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Model Context Protocol (MCP) server in C# giving FISCAL staff access to Zendesk Support and the Help Center. It is built to the standard of [fiscaltec/vitally-mcp](https://github.com/fiscaltec/vitally-mcp) — the same host, OAuth façade, RBAC, audit, telemetry, CI/CD and Azure shape, ported rather than reinvented — and reproduces the functionality of Swifteq's hosted Zendesk MCP server.

**Status: built, not deployed.** The Azure estate in `infra/terraform/` is greenfield and has not been applied (it passes `terraform validate`; it has never been planned — #3). The Zendesk endpoints were written from the API documentation; on 2026-10-28 every **read** tool was run against the live account with a development token in read-only mode, and what that showed was fixed (PR #1). The same day every write tool was exercised against throwaway records (all since deleted): test tickets, an inactive personal macro, draft articles, a test category and section, and a test end user and organisations; every feature-switch refusal was confirmed. That found and fixed `Update_ticket` silently dropping tag changes, `Add_user_to_organization` moving people out of their organisation without `replace_existing`, and a rename reporting the old name. A public reply on an existing ticket was sent too, notifying only the test agent. Not exercised: per-user OAuth and refresh (#4) and the audit trail (#5), both of which need staging (#3); reads as an ordinary agent rather than an admin (#6); and eight shapes the account could not show, among them a reused idempotency key with a different body (#7). Read the open issues rather than trusting this paragraph: it describes 2026-10-28. Code marks each remaining uncertainty `// Unverified against the live API` — writes checked against the documentation only say "docs-checked, not live-tested". See *Staging validation* before trusting any tool against production.

**Provenance.** Issue references written `vitally-mcp#NNN` point at fiscaltec/vitally-mcp, where the lesson was learnt; the design and the code that carries it came across together. vitally-mcp's own `CLAUDE.md` holds the full history — dates, measurements, incident write-ups — behind the rules stated here more briefly. When this file and that one disagree about a shared component, check the code here.

**Key characteristics:**
- 44 tools over tickets, KPI/SLA/CSAT reporting, macros, users and organisations, configuration and the Help Center — see *Tools*.
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
dotnet test ZendeskMcp.sln -c Debug

# Run a single test class
dotnet test ZendeskMcp.sln -c Debug --filter-class "*TicketsToolsTests"

# Start the server in dev mode: no Entra, no Key Vault, every call made with ONE Zendesk OAuth access token
$env:OAuth__NoAuth = "true"
$env:Zendesk__Subdomain = "your-subdomain"
$env:Zendesk__DevelopmentAccessToken = "<a Zendesk OAuth access token>"   # API tokens are being retired
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project ZendeskMcp/ZendeskMcp.csproj
```

`StartupGuards.EnsureDevelopmentTokenIsLocal` refuses the development token unless `OAuth:NoAuth=true` **and** no `Zendesk:KeyVaultUri` is set. Behind real sign-in it would turn a per-user server back into a shared service account, attributing everyone's changes to the token's owner. Do not weaken that guard.

To exercise per-user OAuth locally instead, leave `NoAuth` off, set `Zendesk:OAuthClientId` and `Zendesk:DevelopmentOAuthClientSecret` (in-memory token store; nothing survives a restart), and add `http://localhost:5099/zendesk/callback` to the Zendesk OAuth client's redirect URLs.

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

FISCAL staff point their MCP client at `https://zendesk-mcp.fiscaltec.com/mcp` (decided 2026-10-28; live once #3 lands). They sign in with Microsoft, then connect their own Zendesk account through the one-time link the first Zendesk tool returns. [ACCESS.md](ACCESS.md) is the user-facing guide; keep it in step with any change to either sign-in.

## GitHub issues (issue-driven work)

Track non-trivial work as GitHub issues via the `gh` CLI. Trivial one-off changes don't need an
issue — use judgement. This mirrors the flow used in `searledan/rosetechnologies.co.uk`,
`searledan/dansearle.co.uk` and `searledan/spendy`; the labels below were copied from vitally-mcp on
2026-10-28 (`gh label clone fiscaltec/vitally-mcp --repo fiscaltec/zendesk-mcp`), so names, colours
and descriptions match there.

⚠️ **In a Claude Code cloud session, `gh issue create` fails** with `HTTP 403: GitHub GraphQL is not
available from Claude Code sessions` — it is GraphQL underneath, and the session's GitHub proxy serves
REST only. Create issues through REST instead (`gh api -X POST repos/fiscaltec/zendesk-mcp/issues
--input body.json`, with `title`, `body` and `labels` in the JSON). `gh issue edit` and `gh issue view`
fail the same way (checked 2026-10-28); use `gh api -X PATCH …/issues/<n>` and `gh api …/issues/<n>`.
Hit when #2–#9 were raised. `gh pr merge` is GraphQL too, so merges use the REST endpoint instead
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

1. Pick a `status: ready` issue respecting priority, or create one with type + priority +
   `status: ready`.
2. Flip to in-progress:
   `gh issue edit <n> --remove-label "status: ready" --add-label "status: in-progress"`.
3. Branch `<cc-type>/<short-description>`, where `<cc-type>` is the **Conventional-Commits** type used
   in the PR title — not the issue's type label. They map loosely: `feature` → `feat/…`,
   `documentation`/`content` → `docs/…`, `tech-debt` → `chore/…` or `refactor/…`, `bug` → `fix/…`,
   `ops` → `ci/…` or `chore/…`.
4. Reference `#<n>` in commits where relevant.
5. Open the PR with **`Closes #<n>`**. `.github/workflows/pr-title.yml` enforces the
   Conventional-Commits prefix on the PR *title*, and `main` takes squash merges — so the PR title
   becomes the commit subject on `main`. On squash-merge the issue auto-closes; flip it to
   `status: complete` then, so a finished issue ends up *closed + `status: complete`*, distinguishable
   from one closed as won't-fix or duplicate.
6. If work stalls, swap the status for `status: blocked` and comment what's blocking.

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
`PreToolUse` hook, registered in `.claude/settings.json` under the `Bash` matcher twice — with
`if: "Bash(gh pr merge*)"` and with `if: "Bash(gh api*)"` — that **denies** a merge (`gh pr merge`, or
`gh api` reaching `pulls/<n>/merge` with any method but GET) unless Copilot is not pending, its
latest review's `commit_id` equals the head, and unresolved threads are zero. A GraphQL merge
mutation is refused outright. It exempts Dependabot and **fails closed** — including if
`jq`/`sed`/`awk` are missing or broken, so a damaged toolchain cannot wave a merge through. Ported
from `searledan/dansearle.co.uk`, then moved to REST on 2026-10-28 (#2) after `searledan/spendy`'s
#484: it resolves the repo from the `origin` remote, reads the PR, its timeline and its reviews over
REST, and reads review threads from the cloud session's `ccr/review_threads` route when
`CLAUDE_CODE_REMOTE` is set and through GraphQL locally (REST has no thread-resolution state).
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
- **Run the merge as a standalone command — no pipes, no `;`, no `&&`, no quotes.** The hook refuses
  anything with quotes or shell metacharacters, because it cannot read those with certainty.
- The `if` filters match the command text, so *any* Bash call starting `gh api` or containing
  `gh pr merge` runs the hook — including a heredoc that merely mentions one, which it then denies.
  For a `gh api` call that is not a merge it allows at once, without a network call. Testing the hook
  or editing text that quotes a merge command means doing it from a script file rather than inline.
  Over-matching is the safe direction; don't loosen it.
- **The REST reviews list can lag** (above). The lag only makes the gate deny a merge it would allow;
  wait and re-check.

Verified on porting (2026-08-28) by running the hook against real PRs: it allows vitally-mcp#118, whose Copilot
review was on the merged head, and **denies vitally-mcp#117** — *"Copilot's latest review (6808805…) is not on
the current head (58afb2b…)"* — which is the mistake that prompted the port.

**Forms** — `.github/ISSUE_TEMPLATE/` provides seven, one per type label except `ux`: `bug.yml`,
`feature.yml`, `tech-debt.yml`, `security.yml`, `ops.yml`, `documentation.yml`, `content.yml`.
Filenames match the label they preset, and the shared four match the sibling repos. Each form
presets its type label plus `status: ready`, and its title with the matching Conventional-Commits
prefix; add a `priority:` label after creating. Blank issues stay enabled for quick notes and for
`ux`. Bug and Feature carry server-specific fields (MCP client, server URL, a failure
timestamp for correlating with Application Insights) — keep those if you edit the forms.

`documentation` and `content` both map to `docs/…` branches but cover different audiences:
**Documentation** is prose for humans (`CLAUDE.md`, `README.md`, `docs/`), whereas **Content** is the
copy an *LLM* reads — tool `[Description]` and `Title` values and `ZendeskServerInstructions.Text`.
Content wording changes model behaviour (which tool gets picked, how it's called), so they are
defects rather than cosmetics; the form asks for the observed effect to keep that distinction sharp.

**Writing issues** — aim for "detailed enough to implement without further context":

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

⚠️ **Single replica is a correctness constraint, not a cost choice.** The `/oauth/*` proxy state (inherited from vitally-mcp), connect links, connect attempts and upload slots all live in process memory, and a token removed on one replica stays cached on another for up to `Zendesk:TokenCacheDuration`. `containerapps.tf` pins `max_replicas = 1`. Scaling out means registering a shared `IDistributedCache` (Redis) first and moving the OAuth proxy's `IMemoryCache` state onto it — a registration change for the Zendesk parts, a small code change for the proxy.

### The Entra façade, RBAC, logging and audit — as vitally-mcp

These components are ported without behavioural change, and their rules hold here exactly. vitally-mcp's `CLAUDE.md` carries the evidence for each; the short form:

- **The OAuth proxy is a complete authorisation-server façade.** `issuer` is our origin (RFC 8414 §3.3) and equals `authorization_servers` byte for byte; `/oauth/callback` strips any upstream `iss` and appends ours (RFC 9207); `authorization_response_iss_parameter_supported: true` is honest only because of that. Change none of the three alone (vitally-mcp#90).
- **`redirect_uri` validation is the only thing between the proxy and an open redirector** — loopback on any port plus `OAuth:AllowedClientRedirectUris`. Never widen it.
- **RFC 8707 `resource` is validated, then terminated.** `OAuth:UpstreamResourceScope` set (Entra) drops a matching `resource` and names the API by scope on both `/oauth/authorize` and `/oauth/token` — on a refresh that is what keeps the token's audience right. Relaying `resource` to Entra fails with AADSTS9010010 whatever its spelling (vitally-mcp#105, vitally-mcp#108).
- **`Audience` (no trailing slash, the App ID URI) and `Resource` (with the slash, what Claude Code normalises to) differ by one character on purpose.** Do not reconcile them.
- **Upstream endpoints come from OIDC discovery**, checked against `Authority` (§4.3), cached 12 h with last-known-good fallback; startup refuses to boot without them (vitally-mcp#104).
- **`/oauth/token` services only `authorization_code` and `refresh_token`** — it injects the confidential secret, so any other grant would mint tokens without a sign-in.
- **Entitlement comes from live Entra group membership via Graph** (`Authorization:LiveGroupCheck`, transitive, keyed on `oid` — never `sub`), fresh → stale (up to `LiveGroupStaleSeconds`) → deny. No claim authorises while the live check is on (vitally-mcp#106, vitally-mcp#108, vitally-mcp#156).
- **`ZendeskService.SendAsync` is the enforcement point**; `[Authorize]` discovery filtering shares `HasEffectivePermissionAsync` with it so they cannot disagree. `Authorization:ReadOnly` denies every non-GET for everyone.
- **Log levels are a security control and live in `Program.cs`**, not `appsettings.json` (excluded from the image) or environment variables (lost on a recreate). Failure records log the exception *type*, the status and the resource *type* — never a message, an attached exception, a body or the rest of the path (vitally-mcp#94).
- **Four audit record shapes** — tool call (primary: caller `oid`, tool, arguments, record ids touched, tier, staleness, outcome, correlation id), action (per upstream call), service denial, tier denial. Tool arguments are recorded; upstream response bodies never are. Records route to `AppEvents` when `ApplicationInsights__ConnectionString` is set, and must not depend on trace sampling (vitally-mcp#147, vitally-mcp#178).

What changed in porting them: the policy names (`zendesk:*`), the default custom-permissions claim, the Graph tier groups (`sg-zendesk-*`), the meter and counter names (`ZendeskMcp`, `zendesk.*`), and `AuditRecordIds` (below). `IncludeReads` matters more here than in vitally-mcp: Zendesk's own audit log records changes, not reads, so this trail is the only record of who read which ticket.

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
| `/zendesk/callback` | Requires cookie == `state` (fixed-time compare) — a code completed in another browser is login CSRF. Spends the attempt. Exchanges the code with the client secret **and** the verifier. Reads `/api/v2/users/me` with the new token and refuses unless the role is agent/admin and, with `RequireEmailMatch` (default on), the email equals the sign-in name case-insensitively. A refused token is revoked immediately. Result pages HTML-encode everything Zendesk returned. |
| Sign-in name | `CallerIdentity.TryGetSignInName` prefers `upn` (an optional claim `entra.tf` requests) over `preferred_username`, which Microsoft documents as mutable. |
| Startup guards | `RequireEmailMatch=false` is refused unless `OAuth:NoAuth`; the development token is refused unless NoAuth, no Key Vault **and** the Development environment; connect links and the callback refuse to derive their origin from the request unless NoAuth — behind sign-in `OAuth:PublicBaseUrl` is required. |
| Why the email match matters | Without it, anyone who opens a person's link — forwarded, leaked, or planted in a ticket — attaches **their** Zendesk account to that person's identity, and every later call that person makes acts as the opener. The cookie stops a planted *code*; the email match stops a planted *link*. Keep both. |
| `ZendeskCredentialProvider` | Reads the caller's `oid` (no `oid` → refused); no token → connect link; token within 2 min of expiry → refresh **under a per-person lock** (Zendesk rotates the refresh token, so a second concurrent refresh would present a spent token, fail `invalid_grant`, and disconnect someone who did nothing wrong); refresh refused with `invalid_grant`/`invalid_token`/`unauthorized_client` → forget the token and hand back a link. |
| `ZendeskService` on a `401` | `OnTokenRejectedAsync(rejectedHeader)` decides, **under the person's lock and about the token that was actually sent**: if the stored token has since changed (a refresh or reconnect landed while the request was in flight), retry with it; else, if a refresh token exists, refresh and retry once; only then forget the grant and fail with a fresh connect link. Forgetting "the current token" on any 401 would wipe a working grant that replaced the rejected one. |
| `ZendeskConnectionRequiredException` | Surfaced verbatim by `ToolErrorResult` — its message *is* the link. `ZendeskServerInstructions` tells the model to hand it over unchanged. |
| Refresh persistence | The store caches **before** it writes to Key Vault, and a failed write after a refresh is logged, not thrown: Zendesk has already spent the old refresh token, so losing the new one would disconnect the person. The connect callback stores under the person's lock, so an in-flight refresh cannot overwrite a fresh connection. |
| `KeyVaultZendeskTokenStore` | One secret per person, `{TokenSecretPrefix}{oid}` (`zendesk-user-` in production, `zendesk-staging-user-` in staging, so the targets never share a token), JSON, tagged `kind=zendesk-user-token`, `ExpiresOn` = refresh expiry. Reads cached `TokenCacheDuration`, including "not connected". **Removal writes a tombstone version, never a delete**: soft delete is on, and a soft-deleted name cannot be set again until purged, so a delete would make reconnecting fail for the whole retention window. Older versions keep the old token, which is why disconnecting revokes at Zendesk first. |
| Scopes and lifetimes | `read write`; access tokens requested at 2 h and refresh tokens at 90 days (`AccessTokenExpiresInSeconds` null requests Zendesk's legacy non-expiring token instead). |
| Managed identity | Needs **Key Vault Secrets Officer** on the vault (it writes), which also lets it overwrite `zendesk-oauth-client-secret`. Accepted: the identity already holds every person's token; see `infra/terraform/identity.tf`. |

### ZendeskService — the one path to Zendesk

`AddHttpClient<ZendeskService>()` behind `ZendeskRateLimitHandler`. Every call:

1. **RBAC backstop** — `ToolAuthorizer.EnsureAuthorizedAsync(verb)`: GET → read, POST/PUT/PATCH → write, DELETE → delete. Tools keep that mapping faithful: reads (macro preview included) are GETs, and **a write tool never issues a DELETE** — `Add_user_to_organization` moves a user with a `PUT`, for instance — so the write tier never needs the delete tier.
2. **Credential** — the caller's own `Authorization` header from `ZendeskCredentialProvider`, per request; the shared `HttpClient` is never mutated.
3. **Audit** — `LogAction` per upstream call; record ids captured from the **raw** body by `AuditRecordIds` before any tool reshapes it.
4. **Errors** — a non-2xx becomes `HttpRequestException` carrying Zendesk's body (truncated to 1 KB), which `ToolErrorResult` surfaces: `{"error":"RecordInvalid","details":…}` is what the model needs to correct its call. The server-side record logs status and resource type only.

**Credential boundaries.** The base URL decides where the caller's token goes, and tool arguments can come from a model that has just read untrusted ticket content:

- **Only `*.zendesk.com` hosts ever receive the credential.** `ResolveBrandBaseUrlAsync(brand_url)` accepts only `https` hosts named by the account's own `/api/v2/brands` (cached 10 min), and translates a brand's **host mapping** to that brand's `{subdomain}.zendesk.com` host — a mapping can name any domain, and a stale or malicious one would otherwise receive every reader's token. A Help Center tool passes `brand_url` through the `brandUrl` parameter — never builds a host itself.
- `DownloadAsync(url)` (attachments, embedded article images) applies the same host rule, refuses `/api/` paths (an image `src` must not make each reader's token call the API), and follows redirects itself — the `ZendeskService` handler is built with `AllowAutoRedirect = false` — at most three, https only, to an account host (credential re-sent) or `*.zdusercontent.com` (no credential; signed URLs). Anything else is refused.
- Inlined article images are capped at 2 MB each and 8 MB per call, and only in the main body — a base64 copy per translation per image could otherwise cost gigabytes on the single replica.
- `ZendeskService.Segment()` escapes caller strings used in paths; `BuildUrl` escapes every query value.

**`AuditRecordIds` for Zendesk shapes:** ids are numbers (rendered as raw text); the primary collection is the first array of objects in the body (`tickets`, `results`, `articles` — Zendesk serialises it before sideloads such as `users`), `meta`/`links` ignored; a single record is bare or wrapped (`{"ticket":{…}}`). The mutation-URL fallback accepts only a **numeric** last segment — Zendesk paths also end in locales and collection names, and recording `en-gb` as a record id would be a fabricated identifier.

**Paging.** Tools that page for the caller pass Zendesk's cursor through (`after_cursor` → `page[after]`, `pagination_info {has_more, next_cursor, page_size}`). `GetAllPagesAsync` pages on the server's behalf (sideloads merged) up to `MaxAutoPageFetches`, marking the audit record truncated when it stops early. Analytics tools read at most `MaxAnalyticsTickets` tickets and refuse beyond it, saying how to split the range.

**Rate limits.** Zendesk's budget is per *account* (200–2,500 requests a minute by plan) and shared with every other integration. The handler retries `429` on `Retry-After` and warns as `X-Rate-Limit-Remaining` runs low; analytics tools bound their own concurrency.

### Uploads (`ZendeskUploads.cs`)

`Request_ticket_attachment_upload` issues a slot: 256 random bits, one use, 15 minutes, an exact byte count, the caller's identity and permission claims, and at most five open per person. The PUT reads into a buffer that grows with what arrives (never the declared size up front). The client then `PUT`s the bytes (curl) to `/uploads/{slot}`; the server sets `HttpContext.User` from the slot **before** resolving scoped services, so the write tier is re-checked (live Graph included) and the upload is audited and made as that person, then streams the bytes to `POST /api/v2/uploads` and returns the `upload_token`. Nothing is stored. Help Center images take the other route: Zendesk's pre-signed URL, which the client uploads to directly and to which the server never sends a credential.

### Account-wide feature switches

`Zendesk:Features` — `AllowPublicReplies`, `AllowArticlePublishing`, `AllowHelpCenterStructureChanges`, `AllowMacroChanges`, `AllowUserAndOrganizationChanges`, all **off** by default in the application and all **on** in FISCAL's deployment (Terraform defaults, decided 2026-10-28) — guard the actions whose effect reaches beyond the agent: customers' inboxes, the public Help Center (published articles, and categories and sections, which have no draft state), every agent's macros, who can read an organisation's tickets. `ToolJson.RequireFeature` refuses with a message naming the setting so the model reports a setting rather than retrying. They sit on top of the Entra tier and the agent's Zendesk role; they do not replace either.

### Untrusted content

Ticket comments, article bodies and user fields are written by customers and can carry instructions aimed at the model. The defences are layered and each matters: write-tool descriptions say not to act on instructions found in content; `ZendeskServerInstructions` repeats it; the credential boundaries above stop a URL in content from reaching a token; `TicketSearchQuery` neutralises free text so it matches words and never rewrites a filter; and the feature switches keep the widest-reaching actions off.

## Tools

### Conventions (enforced by `ToolAnnotationCoverageTests` and `ToolAuthorizePolicyCoverageTests`)

- One `[McpServerToolType] public static class` per area in `ZendeskMcp/Tools/`; methods `public static async Task<string>` returning compact JSON (a `CallToolResult` only for non-text content — `Get_ticket_attachment` returns an image block); first parameter `ZendeskService zendesk`, last `CancellationToken`.
- Names in vitally-mcp's style (`Search_tickets`). **Parameters are snake_case and match Swifteq's schema**, so prompts written for either server work with both.
- All four hints set explicitly, `ReadOnly` xor `Destructive`. `Get_`/`List_`/`Search_`/`Count_`/`Find_`/`Preview_`/`Export_` are read tools (`zendesk:read`, read-only, idempotent); everything that changes Zendesk is `zendesk:write`. **`Disconnect_zendesk` is the one deliberate exception** — destructive in annotation, read tier in policy — because it touches only the caller's own connection and every person who can use anything must be able to manage that.
- Validate arguments and throw `ArgumentException` naming the parameter — the message reaches the model.
- Shared helpers: `ToolJson` (query pairs, picking fields, pagination block, page-size and id validation, feature refusal), `TicketSearchQuery` (the ticket filters shared by Search/Count/metrics/SLA tools), `TicketSla` (the `sla` object shared by Get_ticket, Search_tickets, Get_sla_queue and Get_sla_summary).

### Catalogue

| Area | File | Tools |
|---|---|---|
| Connection | `ConnectionTools.cs` | `Get_zendesk_connection`, `Get_zendesk_connect_link`, `Disconnect_zendesk` |
| Tickets | `TicketsTools.cs`, `TicketAttachmentsTools.cs` | `Search_tickets`, `Count_tickets`, `Get_ticket`, `Get_ticket_conversation`, `Create_ticket`, `Update_ticket`, `Get_ticket_attachment`, `Request_ticket_attachment_upload` |
| Reporting | `AnalyticsTools.cs`, `SatisfactionTools.cs`, `TicketSla.cs` | `Get_ticket_metrics_summary`, `Get_sla_summary`, `Get_sla_queue`, `Get_satisfaction_ratings` |
| Macros | `MacrosTools.cs` | `Search_macros`, `Preview_macro`, `Apply_macro`, `Create_macro`, `Update_macro`, `Set_macros_active` |
| People | `UsersTools.cs`, `OrganizationsTools.cs` | `Search_users`, `Find_agent_by_name`, `Search_organizations`, `Add_user_to_organization` |
| Configuration | `ConfigurationTools.cs` | `List_ticket_fields`, `List_ticket_forms`, `List_triggers`, `List_brands` |
| Help Center | `HelpCenterArticlesTools.cs`, `HelpCenterStructureTools.cs`, `HelpCenterMediaTools.cs` | `Search_help_center_articles`, `Get_help_center_article`, `Create_help_center_article`, `Update_article_translation`, `Export_help_center_articles`, `List_help_center_categories`, `Create_help_center_category`, `Update_help_center_category`, `List_help_center_sections`, `Create_help_center_section`, `Update_help_center_section`, `List_user_segments`, `List_permission_groups`, `Request_help_center_image_upload`, `Create_help_center_image` |

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

⚠️ **Run the tests in a shell without the dev-mode variables.** The host-based tests build the app with `WebApplicationFactory`, which reads environment variables, so `OAuth__NoAuth`, `Authorization__ReadOnly`, `Zendesk__DevelopmentAccessToken` and friends left over from running the server locally override the tests' own configuration — about 100 tests fail for that reason alone. CI does not set them. Clear them first, e.g. `env -u OAuth__NoAuth -u Authorization__ReadOnly -u Zendesk__DevelopmentAccessToken -u Zendesk__Subdomain -u ASPNETCORE_ENVIRONMENT dotnet test --solution ZendeskMcp.sln -c Debug`. #8 makes the tests immune and removes this paragraph.

| Area | Tests |
|---|---|
| Per-user OAuth | `ZendeskCredentialProviderTests` (own bearer, connect link with no request made, 401 → forget + link, refresh and rotation stored, one refresh under concurrency, refused refresh, development token), `ZendeskConnectEndpointsTests` (full flow through the host, wrong email / end user refused **and revoked**, missing cookie, one-shot link and state, encoded result page), `ZendeskTokenStoreTests` (secret shape, absence cached, tombstone not delete) |
| Transport | `ZendeskServiceTests` (escaping, error bodies, cursor paging and cap, brand and download host boundaries, RBAC backstop), `AuditRecordIdsTests`, `ZendeskRateLimitHandlerTests` |
| Uploads | `ZendeskUploadsTests` (through the host: exact size, one use, inline, identity and claims carried, tier re-checked) |
| Shared components | everything ported from vitally-mcp — OAuth proxy, discovery, RBAC, Graph resolver, stale entitlement, audit composition, logging filters, telemetry redaction, metrics |
| Tools | `Tools/*ToolsTests` — request built (verb, path, query, body), result shaped, validation, feature switches, credential boundaries |

## Deployment

The same shape as vitally-mcp — Azure Container Apps (production warm, staging scale-to-zero) in a VNet, private Key Vault, the Vitally estate's private ACR **shared** (`vitallyproducruksouth`, repository `zendesk-mcp`; `infra/terraform/acr.tf` sets out the cross-estate write access that costs), Application Insights behind AMPLS, GitHub Actions deploying through OIDC federated credentials, a staging gate and a release train — defined **greenfield** in `infra/terraform/` (see its README for the apply procedure and pending decisions) and `.github/workflows/`. Nothing has been applied yet.

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

Before production, on staging against the real Zendesk account, with `Authorization__ReadOnly=true` except while deliberately testing writes on a test ticket:

1. Sign in (Entra), then connect a Zendesk agent; confirm a mismatched Zendesk login is refused and its token revoked.
2. Run every read tool once, and every write tool against a test ticket, test macro (inactive) and draft article, with the feature switches as production will have them.
3. Check each place the code says `// Unverified against the live API` — the response shapes in particular — and remove the marker once confirmed. The read-side markers were settled live on 2026-10-28; what remains is write endpoints (docs-checked only) and shapes the validation account could not show (no messaging tickets, checkbox fields, custom objects, macro attachments or next-reply SLA targets; one Help Center locale; agent and account in the same time zone).
4. Confirm refresh: leave a connection past its access-token expiry and call again.
5. Confirm the audit records in `AppEvents` (`AppRoleName` separates staging) carry the expected ids.
6. Disconnect, and confirm the token is revoked in Zendesk and the secret reads as a tombstone.

Where each step stands, mapped to its issue (2026-10-28):

| Step | State | Issue |
|---|---|---|
| Staging itself | not applied; blocked on decisions recorded in the issue | #3 |
| 1 — sign-in, connect, mismatched login refused and revoked | not exercised; becomes a standing runbook, `docs/runbooks/zendesk-oauth-staging-validation.md`, modelled on vitally-mcp's `entra-cutover-staging-validation.md` | #4 |
| 2 — every read and write tool | **done live** with a development token (PR #1), as an **admin**; repeating the reads as an ordinary agent is open | #6 |
| 3 — `Unverified` markers | read-side settled; eight remain that need data the account lacks | #7 |
| 4 — refresh | not exercised | #4 |
| 5 — audit records in `AppEvents` | not exercised | #5 |
| 6 — disconnect, revoke, tombstone | not exercised | #4 |

⚠️ **There is one Zendesk account and no sandbox**, as vitally-mcp has one Vitally tenant: staging
writes reach real customers. Lift `Authorization__ReadOnly` only for the write checks, with
vitally-mcp's two independent restores (a shell trap plus a detached failsafe that reads the guard back
off the revision actually serving traffic). Creating a ticket also fires the *Notify all agents of
received request* trigger, which emails every agent. PR #1's write tests paused that trigger for the
seconds around each create; #4 records how staging should handle it.

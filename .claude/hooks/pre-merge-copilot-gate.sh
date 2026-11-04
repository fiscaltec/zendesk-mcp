#!/usr/bin/env bash
# PreToolUse (Bash) hook — block a merge of a human PR until Copilot's review cycle for the
# current HEAD commit is complete and clean.
#
# Two merge commands are gated, both registered in .claude/settings.json:
#   * `gh pr merge <n> --squash --match-head-commit <sha>`          (`if: "Bash(gh pr merge*)"`)
#   * `gh api -X PUT repos/{owner}/{repo}/pulls/<n>/merge -f merge_method=squash -f sha=<sha>`
#                                                                   (`if: "Bash(gh api*)"`)
# The second is the documented form: `gh pr merge` is GraphQL underneath, and Claude Code cloud
# sessions refuse GraphQL outright, so it cannot merge there at all. Everything this hook reads
# goes through REST for the same reason, except review threads when NOT in a cloud session (REST
# has no thread-resolution state; the cloud session's `ccr` route has, and GraphQL has locally).
#
# Blocks via the PreToolUse permissionDecision JSON. Fail-closed: if any check cannot be
# verified, deny rather than risk a premature merge. Dependabot PRs are exempt (CI-gated
# auto-merge by design). Tested by .github/scripts/tests/pre-merge-copilot-gate.test.sh.
set -uo pipefail

# Deny the tool call with a reason (PreToolUse JSON mechanism; exit 0).
deny() {
	printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":%s}}\n' \
		"$(printf 'Copilot merge gate — %s' "$1" | jq -Rs .)"
	exit 0
}
# Allow: emit nothing and exit 0 → defer to Claude Code's normal permission flow.
allow() { exit 0; }

# deny() builds its JSON with jq, so the gate would fail *open* if jq were
# missing OR present-but-broken. Prove jq actually runs (not just that it's on
# PATH) with a static denial fallback (printf is a shell builtin — no external
# tool needed) so a broken toolchain can never wave a merge through.
if ! printf '{}' | jq -e . >/dev/null 2>&1 \
	|| ! printf 'x' | sed 's/x/y/' >/dev/null 2>&1 \
	|| ! awk 'BEGIN { exit 0 }' >/dev/null 2>&1; then
	printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Copilot merge gate — a required tool (jq/sed/awk) is missing or not runnable; cannot verify the gate (failing closed)"}}\n'
	exit 0
fi

input=$(cat)
# Fail closed if the payload can't be parsed or carries no command — we can't
# confirm the call is safe, and the `if` filters mean we only get here for gh commands.
cmd=$(printf '%s' "$input" | jq -er '.tool_input.command' 2>/dev/null) \
	|| deny "could not read the command from hook input (failing closed)"

# The logins Copilot's reviewer appears under, depending on which API answered (searledan/spendy
# `.claude/scripts/github.mjs` lists the same three). Exact matches only.
COPILOT_LOGINS='["Copilot","copilot-pull-request-reviewer[bot]","copilot-pull-request-reviewer"]'

# ---------------------------------------------------------------------------------------------
# 1. Is this a merge at all? (Bash regex — no external grep, so a broken grep can't fail open.)
# ---------------------------------------------------------------------------------------------
form=""
if [[ "$cmd" =~ (^|[[:space:]])gh[[:space:]]+pr[[:space:]]+merge([[:space:]]|$) ]]; then
	form="gh-pr-merge"
elif [[ "$cmd" =~ (^|[[:space:]])gh[[:space:]]+api([[:space:]]|$) ]]; then
	# A GraphQL merge mutation is a merge by another route: refuse it rather than parse it.
	[[ "$cmd" =~ (mergePullRequest|enablePullRequestAutoMerge) ]] \
		&& deny "merging through a GraphQL mutation is not gated; use the REST form: gh api -X PUT repos/{owner}/{repo}/pulls/<n>/merge -f merge_method=squash -f sha=<head> (failing closed)"
	# A GraphQL query read from a file (-F query=@file) or a body (--input) is invisible here, so
	# it could carry that mutation unseen: refuse it rather than allow what cannot be read.
	[[ "$cmd" =~ (^|[[:space:]])graphql([[:space:]]|$) ]] && [[ "$cmd" =~ (=@|[[:space:]]--input([[:space:]=]|$)) ]] \
		&& deny "a GraphQL request whose query comes from a file or --input cannot be checked for a merge mutation; pass the query inline (failing closed)"
	[[ "$cmd" =~ pulls/[^[:space:]]*/merge ]] && form="rest-merge"
fi
[ -n "$form" ] || allow

# ---------------------------------------------------------------------------------------------
# 2. Read the command with certainty or not at all.
#
# Finding a flag inside an arbitrary command string has been bypassed three different ways in
# this hook's history: a glob matched it inside `--body=…`, whitespace-splitting matched it inside
# a quoted value, and taking the FIRST occurrence disagreed with gh, which uses the LAST. So the
# gate refuses what it cannot read exactly: no quotes, no shell metacharacters. A hex SHA never
# needs quoting, `{owner}`/`{repo}` need none, and the PR title already becomes the squash subject
# here, so a plain command always exists. With those gone, splitting on whitespace is exact.
# ---------------------------------------------------------------------------------------------
case "$cmd" in
*[\"\'\`\$\;\|\&\<\>\(\)\\]*)
	deny "the merge command contains quotes or shell metacharacters, so this gate cannot read its arguments with certainty. Write it plainly, e.g. gh api -X PUT repos/{owner}/{repo}/pulls/<n>/merge -f merge_method=squash -f sha=<head> (failing closed)" ;;
esac

# Cross-repo or cross-host merges can't be verified against the current repo — fail closed.
[[ "$cmd" =~ (^|[[:space:]])(-R|--repo|--hostname)([[:space:]=]|$) ]] \
	&& deny "cross-repo or cross-host merge (-R/--repo/--hostname) is not supported by the gate (failing closed)"

# ---------------------------------------------------------------------------------------------
# 3. The current repo, from origin — not `gh repo view`, which is GraphQL. Accepts
#    https://github.com/o/r(.git), git@github.com:o/r.git and the cloud session's proxy URL
#    (…/git/o/r): owner and name are the last two path segments.
# ---------------------------------------------------------------------------------------------
url=$(git remote get-url origin 2>/dev/null) || url=""
url=${url%.git}; url=${url%/}
name=${url##*/}; rest=${url%/*}; owner=${rest##*[/:]}
[[ "$owner" =~ ^[A-Za-z0-9_.-]+$ && "$name" =~ ^[A-Za-z0-9_.-]+$ ]] \
	|| deny "could not resolve the current repo from the origin remote (failing closed)"
nwo="$owner/$name"

# ---------------------------------------------------------------------------------------------
# 4. Which PR.
# ---------------------------------------------------------------------------------------------
pr=""
if [ "$form" = "rest-merge" ]; then
	# Exactly one token names the merge endpoint; its repo must be this one ({owner}/{repo} is gh's
	# placeholder for it). Method: an explicit GET is a read-only "is it merged?" check and is
	# allowed; anything else that reaches this endpoint (PUT, or POST by gh's default once a field
	# is given) is a merge attempt and is gated.
	read -r ptoken pcount method < <(printf '%s' "$cmd" | awk '
		{
			for (i = 1; i <= NF; i++) {
				t = $i
				if (t ~ /pulls\/[^\/]*\/merge\/?$/) { n++; p = t }
				if (t == "-X" || t == "--method") { m = toupper($(i + 1)) }
				else if (t ~ /^-X./) { m = toupper(substr(t, 3)) }
				else if (t ~ /^--method=/) { m = toupper(substr(t, 10)) }
			}
			printf "%s %d %s\n", (p == "" ? "-" : p), n, (m == "" ? "-" : m)
		}')
	[ "$method" = "GET" ] && allow
	[ "$pcount" = "1" ] || deny "the merge command names the merge endpoint $pcount times; give it exactly once (failing closed)"
	path=${ptoken#/}; path=${path%/}
	if [[ "$path" =~ ^repos/([^/]+)/([^/]+)/pulls/([0-9]+)/merge$ ]]; then
		powner=${BASH_REMATCH[1]}; pname=${BASH_REMATCH[2]}; pr=${BASH_REMATCH[3]}
	else
		deny "could not read a PR number from '$ptoken'; use repos/{owner}/{repo}/pulls/<n>/merge (failing closed)"
	fi
	if ! { [ "$powner" = "{owner}" ] && [ "$pname" = "{repo}" ]; } \
		&& ! { [ "${powner,,}" = "${owner,,}" ] && [ "${pname,,}" = "${name,,}" ]; }; then
		deny "the merge targets $powner/$pname, not the current repo $nwo (failing closed)"
	fi
	# The body must come from fields this gate can read, not a file it cannot.
	[[ "$cmd" =~ (^|[[:space:]])--input([[:space:]=]|$) ]] \
		&& deny "--input sends a request body this gate cannot read; give the fields with -f (failing closed)"
else
	# `gh pr merge`: first non-flag token after `merge`, skipping the values consumed by
	# value-taking flags (so `--subject Foo` isn't mistaken for the PR). One candidate → that PR;
	# none → the current branch's PR; more than one → ambiguous, deny.
	arg=$(printf '%s' "$cmd" | sed -nE 's/.*gh[[:space:]]+pr[[:space:]]+merge[[:space:]]+//p' | awk '
		BEGIN { split("-t --subject -b --body -F --body-file --author-email --match-head-commit", f, " "); for (k in f) vf[f[k]] = 1 }
		{
			n = 0
			for (i = 1; i <= NF; i++) {
				t = $i
				if (t ~ /^-/) { if (t !~ /=/ && (t in vf)) i++; continue }
				cand[++n] = t
			}
			if (n == 1) print cand[1]
			else if (n > 1) print "__AMBIGUOUS__"
		}')
	[ "$arg" = "__AMBIGUOUS__" ] && deny "ambiguous PR argument in the merge command (failing closed)"
	branch=""
	if [[ "$arg" =~ ^[0-9]+$ ]]; then
		pr=$arg
	elif [[ "$arg" == *://* ]]; then
		[[ "${arg,,}" == *"/${owner,,}/${name,,}/pull/"* ]] || deny "PR URL targets a different repo than $nwo (failing closed)"
		[[ "$arg" =~ /pull/([0-9]+)(/|$) ]] && pr=${BASH_REMATCH[1]}
		[ -n "$pr" ] || deny "could not resolve a PR from '$arg' (failing closed)"
	elif [ -n "$arg" ]; then
		branch=$arg
	else
		branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null) || branch=""
		[ -n "$branch" ] && [ "$branch" != "HEAD" ] || deny "no PR resolvable for the current branch to gate (failing closed)"
	fi
	if [ -z "$pr" ]; then
		[[ "$branch" =~ ^[A-Za-z0-9._/-]+$ ]] || deny "could not resolve a PR from '$branch' (failing closed)"
		found=$(gh api "repos/$nwo/pulls?state=open&head=$owner:$branch&per_page=2" --jq '[.[].number] | "\(length) \(.[0] // "")"' 2>/dev/null) \
			|| deny "could not look up the open PR for branch '$branch' (failing closed)"
		[ "${found%% *}" = "1" ] || deny "expected exactly one open PR for branch '$branch', found ${found%% *} (failing closed)"
		pr=${found#* }
	fi
fi

# ---------------------------------------------------------------------------------------------
# 5. The PR's facts, over REST.
# ---------------------------------------------------------------------------------------------
facts=$(gh api "repos/$nwo/pulls/$pr" --jq '"\(.number) \(.head.sha // "") \(.user.login // "")"' 2>/dev/null) \
	|| deny "could not query PR #$pr (failing closed)"
read -r prnumber apihead author <<<"$facts"

# Dependabot carve-out — deliberately the FIRST thing after reading the facts: an exemption that
# only applies when nothing else went wrong is not an exemption. REST reports `dependabot[bot]`;
# `app/dependabot` is the GraphQL spelling, kept so the carve-out survives either.
case "$author" in
dependabot\[bot\] | app/dependabot) allow ;;
esac

# `head.sha`, like GraphQL's `headRefOid`, can LAG after a push — vitally-mcp recorded it naming
# the previous commit seconds after one on vitally-mcp#122. Trusting it fails OPEN: in that window
# it names the commit Copilot already reviewed, the comparison below matches, and a newly pushed,
# UNREVIEWED commit merges — vitally-mcp#117, the mistake this hook exists to stop.
# `refs/pull/N/head`, read from the git server by `git ls-remote`, is authoritative and does not
# lag. GitHub publishes it on the base repo for every PR, forks included, so there is no
# branch-name or local-checkout fallback (an earlier version had one and compared unrelated commits).
[ -n "$prnumber" ] && [ "$prnumber" = "$pr" ] || deny "could not read the PR number for '$pr' (failing closed)"
[ -n "$apihead" ] || deny "could not read the head commit of PR #$pr (failing closed)"
head=$(git ls-remote origin "refs/pull/$pr/head" 2>/dev/null | awk 'NR==1 {print $1}') || head=""
[ -n "$head" ] || deny "could not resolve refs/pull/$pr/head from origin — no authoritative, non-lagging head is available, and the only other value is the one known to lag (failing closed)"
[ "$apihead" = "$head" ] \
	|| deny "PR #$pr reports head $apihead but refs/pull/$pr/head is at $head — the PR API lags after a push, and the older value would match an already-reviewed commit. Wait for it to catch up, then re-check (failing closed)"

# ---------------------------------------------------------------------------------------------
# 6. The pin. Everything here is true of ONE MOMENT, and the merge runs afterwards — a push in
#    that window would merge a commit this gate never saw. The hook cannot rewrite the command, so
#    it requires the caller to pin the merge to the verified head, which makes GitHub itself
#    refuse the merge if the head moved: `--match-head-commit` for gh, the `sha` field for REST.
#    Take every occurrence (gh and REST both use the LAST); two different pins is a contradiction.
# ---------------------------------------------------------------------------------------------
if [ "$form" = "rest-merge" ]; then
	pinned=$(printf '%s' "$cmd" | awk '
		{
			for (i = 1; i <= NF; i++) {
				t = $i; v = ""
				if (t == "-f" || t == "-F" || t == "--field" || t == "--raw-field") { v = $(++i) }
				else if (t ~ /^--(raw-)?field=/) { v = substr(t, index(t, "=") + 1) }
				else if (t ~ /^-[fF]./) { v = substr(t, 3) }
				if (index(v, "sha=") == 1) { n++; s = substr(v, 5) }
			}
			if (n > 1) { print "DUPLICATE"; exit }
			if (n == 1) { print s }
		}')
	[ "$pinned" = "DUPLICATE" ] && deny "the sha field is given more than once; this gate cannot tell which you meant (failing closed)"
	[ -n "$pinned" ] || deny "pin the merge to the commit this gate verified: add -f sha=$head (without it a push between this check and the merge lands an unreviewed commit; failing closed)"
	# The REST endpoint compares the full SHA, so require exactly the verified head.
	[ "$pinned" = "$head" ] || deny "sha=$pinned is not the head this gate verified ($head) — give the full 40-character SHA of the verified head (failing closed)"
else
	pinned=$(printf '%s' "$cmd" | awk '
		BEGIN { split("-t --subject -b --body -F --body-file --author-email", f, " "); for (k in f) vf[f[k]] = 1 }
		{
			for (i = 1; i <= NF; i++) {
				t = $i
				if (t == "--match-head-commit") { n++; v = $(++i); continue }
				if (index(t, "--match-head-commit=") == 1) { n++; v = substr(t, length("--match-head-commit=") + 1); continue }
				if (t in vf) { i++; continue }
			}
			if (n > 1) { print "DUPLICATE"; exit }
			if (n == 1) { print v }
		}')
	[ "$pinned" = "DUPLICATE" ] && deny "--match-head-commit is given more than once; gh would use the last and this gate cannot tell which you meant (failing closed)"
	[ -n "$pinned" ] || deny "pin the merge to the commit this gate verified: add --match-head-commit $head as its own option (without it a push between this check and the merge lands an unreviewed commit; failing closed)"
	# Abbreviations as a prefix, as git does — but at least git's default length of seven.
	[ "${#pinned}" -ge 7 ] || deny "--match-head-commit $pinned is too short to identify a commit; use at least 7 characters (failing closed)"
	case "$head" in
	"$pinned"*) ;;
	*) deny "--match-head-commit $pinned is not the head this gate verified ($head) — refusing to pin the merge to a different commit (failing closed)" ;;
	esac
fi

# ---------------------------------------------------------------------------------------------
# 7. (1) Copilot must not be mid-review. `requested_reviewers` never lists Copilot (spendy's
#    github.mjs records this), so the issue timeline is the signal: pending when Copilot's latest
#    request/removal/review event is a request. --paginate, because the newest events are not on
#    page one of a long PR (CLAUDE.md, vitally-mcp#162).
# ---------------------------------------------------------------------------------------------
events=$(gh api --paginate "repos/$nwo/issues/$pr/timeline?per_page=100" \
	--jq ".[] | select(((.event == \"review_requested\" or .event == \"review_request_removed\") and ((.requested_reviewer.login // \"\") as \$l | $COPILOT_LOGINS | index(\$l) != null)) or (.event == \"reviewed\" and ((.user.login // \"\") as \$l | $COPILOT_LOGINS | index(\$l) != null))) | .event" 2>/dev/null) \
	|| deny "could not read the timeline of PR #$pr (failing closed)"
lastevent=$(printf '%s\n' "$events" | awk 'NF { e = $0 } END { print e }')
[ "$lastevent" = "review_requested" ] && deny "Copilot is still a requested reviewer on PR #$pr (review pending)"

# (2) Copilot's latest review must target the current head commit.
#
#     ⚠️ READ OVER REST FOR NOW, KNOWING IT CAN LAG. On vitally-mcp#129 (2026-09-15) the REST
#     reviews collection reported Copilot's latest review nearly four hours and nine reviews
#     behind GraphQL. GraphQL is refused in Claude Code cloud sessions, so this reads REST
#     everywhere until that changes. The lag fails CLOSED here — a stale view names an older
#     commit, never the new head — so it can only make the gate deny a merge it should allow
#     ("not on the current head"), never allow one it should deny. If that happens, wait and
#     re-check. --paginate removes the old 100-review window.
reviews=$(gh api --paginate "repos/$nwo/pulls/$pr/reviews?per_page=100" \
	--jq ".[] | select(.submitted_at != null and ((.user.login // \"\") as \$l | $COPILOT_LOGINS | index(\$l) != null)) | \"\\(.submitted_at) \\(.commit_id)\"" 2>/dev/null) \
	|| deny "could not read reviews for PR #$pr (failing closed)"
last=$(printf '%s\n' "$reviews" | awk 'NF' | sort | awk 'END { print $2 }')
[ -n "$last" ] || deny "Copilot has not reviewed PR #$pr yet"
[ "$last" = "$head" ] || deny "Copilot's latest review ($last) is not on the current head ($head) — re-review pending on PR #$pr (if one was just submitted, the REST reviews list can lag; wait and re-check)"

# (3) Zero unresolved review threads, outdated ones included (the ruleset requires every thread
#     resolved). REST has no resolution state: in a cloud session use its `ccr/review_threads`
#     route; locally, GraphQL. Neither is paged here, so fail closed at 100 — the tail could hide
#     an open thread.
if [ -n "${CLAUDE_CODE_REMOTE:-}" ]; then
	threads=$(gh api "repos/$nwo/pulls/$pr/ccr/review_threads" \
		--jq '"\([.[] | select(.resolved != true)] | length) \(length >= 100)"' 2>/dev/null) \
		|| deny "could not read review threads for PR #$pr (failing closed)"
else
	threads=$(gh api graphql -f owner="$owner" -f name="$name" -F number="$pr" \
		-f query='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100){nodes{isResolved} pageInfo{hasNextPage}}}}}' \
		--jq '.data.repository.pullRequest.reviewThreads | "\([.nodes[] | select(.isResolved == false)] | length) \(.pageInfo.hasNextPage)"' 2>/dev/null) \
		|| deny "could not read review threads for PR #$pr (failing closed)"
fi
unresolved=${threads%% *}
morepages=${threads#* }
[ "$morepages" = "false" ] || deny "PR #$pr has 100 or more review threads — cannot verify the tail (failing closed)"
[[ "$unresolved" =~ ^[0-9]+$ ]] || deny "could not count unresolved review threads on PR #$pr (failing closed)"
[ "$unresolved" = "0" ] || deny "$unresolved unresolved review thread(s) on PR #$pr"

allow

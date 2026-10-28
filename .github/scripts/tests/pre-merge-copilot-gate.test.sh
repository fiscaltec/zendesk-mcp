#!/usr/bin/env bash
#
# Tests for .claude/hooks/pre-merge-copilot-gate.sh, run against stub `gh` and `git`
# (tests/fake-gh, tests/fake-git) so every case can be produced on demand — above all the ALLOW
# path, which a live PR only shows you once Copilot has reviewed its head, and the failure shapes a
# live API rarely shows you: a refused call, a lagging head, a pending review with no timestamp.
#
# Run:  bash .github/scripts/tests/pre-merge-copilot-gate.test.sh
#
# The hook allows by printing nothing and denies by printing PreToolUse JSON; it exits 0 either way.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK="$HERE/../../../.claude/hooks/pre-merge-copilot-gate.sh"

pass=0
fail=0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

HEAD_SHA=1111111111111111111111111111111111111111
OLD_SHA=2222222222222222222222222222222222222222
REPO=repos_acme_widget

# A fresh case: stub binaries first on PATH, and a fixture set describing a PR (#7 on
# acme/widget) that should pass — Copilot reviewed the head, nothing pending, no open threads.
# Each test then breaks exactly one thing.
new_case() {
	CASE="$WORK/case$((pass + fail + 1))"
	mkdir -p "$CASE/fixtures" "$CASE/bin"
	cp "$HERE/fake-gh" "$CASE/bin/gh"
	cp "$HERE/fake-git" "$CASE/bin/git"
	chmod +x "$CASE/bin/gh" "$CASE/bin/git"
	export FAKE_GH_DIR="$CASE/fixtures"
	export FAKE_ORIGIN="https://github.com/acme/widget.git" FAKE_HEAD="$HEAD_SHA" FAKE_BRANCH="feat/thing"
	export CLAUDE_CODE_REMOTE=true
	F="$CASE/fixtures"
	echo "{\"number\":7,\"head\":{\"sha\":\"$HEAD_SHA\"},\"user\":{\"login\":\"someone\"}}" >"$F/${REPO}_pulls_7.json"
	echo '[{"number":7}]' >"$F/${REPO}_pulls.json"
	echo '[{"event":"review_requested","requested_reviewer":{"login":"Copilot"}},{"event":"reviewed","user":{"login":"copilot-pull-request-reviewer[bot]"}}]' \
		>"$F/${REPO}_issues_7_timeline.json"
	echo "[{\"user\":{\"login\":\"copilot-pull-request-reviewer[bot]\"},\"submitted_at\":\"2026-10-01T09:00:00Z\",\"commit_id\":\"$OLD_SHA\"},
	       {\"user\":{\"login\":\"copilot-pull-request-reviewer[bot]\"},\"submitted_at\":\"2026-10-02T09:00:00Z\",\"commit_id\":\"$HEAD_SHA\"},
	       {\"user\":{\"login\":\"someone-else\"},\"submitted_at\":\"2026-10-03T09:00:00Z\",\"commit_id\":\"$OLD_SHA\"}]" \
		>"$F/${REPO}_pulls_7_reviews.json"
	echo '[{"resolved":true,"outdated":false,"comment_ids":[1]}]' >"$F/${REPO}_pulls_7_ccr_review_threads.json"
	echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"isResolved":true}],"pageInfo":{"hasNextPage":false}}}}}}' >"$F/graphql.json"
}

run_hook() { # $1 = the command the hook is asked about
	jq -nc --arg c "$1" '{tool_name:"Bash", tool_input:{command:$c}}' \
		| PATH="$CASE/bin:$PATH" bash "$HOOK" 2>/dev/null
}

expect_allow() { # $1 = name, $2 = command
	local out
	out=$(run_hook "$2")
	if [ -z "$out" ]; then
		pass=$((pass + 1))
	else
		fail=$((fail + 1))
		echo "FAIL (expected allow): $1"
		echo "  got: $out"
	fi
}

expect_deny() { # $1 = name, $2 = command, $3 = substring the reason must contain
	local out reason
	out=$(run_hook "$2")
	reason=$(printf '%s' "$out" | jq -r 'select(.hookSpecificOutput.permissionDecision == "deny") | .hookSpecificOutput.permissionDecisionReason' 2>/dev/null)
	if [ -n "$reason" ] && [[ "$reason" == *"$3"* ]]; then
		pass=$((pass + 1))
	else
		fail=$((fail + 1))
		echo "FAIL (expected deny containing '$3'): $1"
		echo "  got: ${out:-<allow>}"
	fi
}

REST="gh api -X PUT repos/{owner}/{repo}/pulls/7/merge -f merge_method=squash -f sha=$HEAD_SHA"

# --- the allow path, both forms -------------------------------------------------------------------
new_case; expect_allow "REST merge of a clean PR" "$REST"
new_case; expect_allow "REST merge naming the repo literally" "gh api -X PUT repos/acme/widget/pulls/7/merge -f merge_method=squash -f sha=$HEAD_SHA"
new_case; expect_allow "gh pr merge of a clean PR, pinned" "gh pr merge 7 --squash --match-head-commit $HEAD_SHA"
new_case; expect_allow "gh pr merge from the branch, pinned" "gh pr merge --squash --match-head-commit ${HEAD_SHA:0:7}"
new_case; unset CLAUDE_CODE_REMOTE; expect_allow "local session reads threads through GraphQL" "$REST"
new_case; echo '{"number":7,"head":{"sha":"'"$HEAD_SHA"'"},"user":{"login":"dependabot[bot]"}}' >"$F/${REPO}_pulls_7.json"
rm "$F/${REPO}_pulls_7_reviews.json"; expect_allow "Dependabot is exempt (no reviews needed)" "$REST"

# --- not a merge: no gating -----------------------------------------------------------------------
new_case; expect_allow "an unrelated gh api call" "gh api repos/{owner}/{repo}/pulls/7"
new_case; expect_allow "an explicit GET of the merge endpoint" "gh api -X GET repos/{owner}/{repo}/pulls/7/merge"

# --- reading the command --------------------------------------------------------------------------
new_case; expect_deny "quoted path" "gh api -X PUT 'repos/{owner}/{repo}/pulls/7/merge' -f sha=$HEAD_SHA" "quotes or shell metacharacters"
new_case; expect_deny "chained command" "$REST && echo done" "quotes or shell metacharacters"
new_case; expect_deny "another repo" "gh api -X PUT repos/other/widget/pulls/7/merge -f sha=$HEAD_SHA" "not the current repo"
new_case; expect_deny "--hostname" "$REST --hostname example.com" "cross-repo or cross-host"
new_case; expect_deny "--input body" "gh api -X PUT repos/{owner}/{repo}/pulls/7/merge --input body.json" "--input"
new_case; expect_deny "GraphQL merge mutation" "gh api graphql -f query=mutation{mergePullRequest}" "GraphQL mutation"

# --- the pin --------------------------------------------------------------------------------------
new_case; expect_deny "REST merge without sha" "gh api -X PUT repos/{owner}/{repo}/pulls/7/merge -f merge_method=squash" "add -f sha=$HEAD_SHA"
new_case; expect_deny "REST merge pinned to another commit" "gh api -X PUT repos/{owner}/{repo}/pulls/7/merge -f sha=$OLD_SHA" "is not the head this gate verified"
new_case; expect_deny "REST merge with an abbreviated sha" "gh api -X PUT repos/{owner}/{repo}/pulls/7/merge -f sha=${HEAD_SHA:0:7}" "full 40-character SHA"
new_case; expect_deny "REST merge with two sha fields" "$REST -f sha=$OLD_SHA" "more than once"
new_case; expect_deny "gh pr merge without a pin" "gh pr merge 7 --squash" "--match-head-commit $HEAD_SHA"

# --- the facts ------------------------------------------------------------------------------------
new_case; unset FAKE_ORIGIN; expect_deny "origin unreadable" "$REST" "could not resolve the current repo"
new_case; unset FAKE_HEAD; expect_deny "refs/pull head unreadable" "$REST" "could not resolve refs/pull/7/head"
new_case; echo '{"number":7,"head":{"sha":"'"$OLD_SHA"'"},"user":{"login":"someone"}}' >"$F/${REPO}_pulls_7.json"
expect_deny "PR API head lags the git ref" "$REST" "the PR API lags"
new_case; rm "$F/${REPO}_pulls_7.json"; expect_deny "PR lookup refused" "$REST" "could not query PR #7"

# --- Copilot --------------------------------------------------------------------------------------
new_case; echo '[{"event":"reviewed","user":{"login":"Copilot"}},{"event":"review_requested","requested_reviewer":{"login":"Copilot"}}]' >"$F/${REPO}_issues_7_timeline.json"
expect_deny "Copilot re-requested and pending" "$REST" "still a requested reviewer"
new_case; echo '[{"event":"review_requested","requested_reviewer":{"login":"Copilot"}},{"event":"review_request_removed","requested_reviewer":{"login":"Copilot"}}]' >"$F/${REPO}_issues_7_timeline.json"
echo "[{\"user\":{\"login\":\"Copilot\"},\"submitted_at\":\"2026-10-02T09:00:00Z\",\"commit_id\":\"$HEAD_SHA\"}]" >"$F/${REPO}_pulls_7_reviews.json"
expect_allow "a removed request is not pending" "$REST"
new_case; rm "$F/${REPO}_issues_7_timeline.json"; expect_deny "timeline refused" "$REST" "could not read the timeline"
new_case; echo '[{"user":{"login":"someone-else"},"submitted_at":"2026-10-02T09:00:00Z","commit_id":"'"$HEAD_SHA"'"}]' >"$F/${REPO}_pulls_7_reviews.json"
expect_deny "no Copilot review" "$REST" "has not reviewed PR #7 yet"
new_case; echo '[{"user":{"login":"copilot-pull-request-reviewer[bot]"},"submitted_at":"2026-10-02T09:00:00Z","commit_id":"'"$OLD_SHA"'"}]' >"$F/${REPO}_pulls_7_reviews.json"
expect_deny "Copilot reviewed an older commit" "$REST" "is not on the current head"
new_case; echo '[{"user":{"login":"Copilot"},"submitted_at":"2026-10-02T09:00:00Z","commit_id":"'"$HEAD_SHA"'"},{"user":{"login":"Copilot"},"submitted_at":null,"commit_id":"'"$OLD_SHA"'"}]' >"$F/${REPO}_pulls_7_reviews.json"
expect_allow "an unsubmitted review does not count as the latest" "$REST"
new_case; rm "$F/${REPO}_pulls_7_reviews.json"; expect_deny "reviews refused" "$REST" "could not read reviews"

# --- threads --------------------------------------------------------------------------------------
new_case; echo '[{"resolved":true,"comment_ids":[1]},{"resolved":false,"outdated":true,"comment_ids":[2]}]' >"$F/${REPO}_pulls_7_ccr_review_threads.json"
expect_deny "an unresolved outdated thread (cloud)" "$REST" "1 unresolved review thread"
new_case; jq -n '[range(100) | {resolved:true,comment_ids:[.]}]' >"$F/${REPO}_pulls_7_ccr_review_threads.json"
expect_deny "100 threads (cloud): tail unverifiable" "$REST" "cannot verify the tail"
new_case; rm "$F/${REPO}_pulls_7_ccr_review_threads.json"; expect_deny "threads refused (cloud)" "$REST" "could not read review threads"
new_case; unset CLAUDE_CODE_REMOTE
echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"isResolved":false}],"pageInfo":{"hasNextPage":false}}}}}}' >"$F/graphql.json"
expect_deny "an unresolved thread (local)" "$REST" "1 unresolved review thread"

echo "pre-merge-copilot-gate: $pass passed, $fail failed"
[ "$fail" -eq 0 ]

#!/usr/bin/env bash
#
# Moves a GitHub issue to one `status:` label, and assigns or unassigns the issue owner to match,
# over REST, so it works in a Claude Code cloud session (where `gh issue edit` is GraphQL and
# refused). Ported from searledan/spendy's `.claude/scripts/github.mjs issue-status`; the assignee
# rule is this repository's (CLAUDE.md -> GitHub issues -> Lifecycle).
#
# Usage:  bash .claude/scripts/issue-status.sh <issue> <ready|in-progress|blocked|complete>
#
#   in-progress            adds the assignee (the issue is being actively worked)
#   ready|blocked|complete removes the assignee (nobody is working it)
#
# The assignee is $ISSUE_ASSIGNEE, default `searledan` (the repository owner, who asked to be
# assigned only while an issue is actively worked). ISSUE_ASSIGNEE= (empty) leaves assignees alone.
# The repository is gh's `{owner}/{repo}`, resolved from the current checkout's `origin`.
#
# Exit codes:
#   0  the issue now has exactly the requested status label, and the assignee rule holds
#   1  a call failed, or the read-back does not show the requested state (nothing is assumed)
#  64  usage error (no API call made)
#
# Order matters: the new label is added BEFORE the old ones are removed, so a failure part-way
# leaves the issue with two status labels (visible, and corrected by re-running) rather than none.
# The target label is checked to exist first, because adding a label GitHub does not know silently
# CREATES it, which would leave a stray label in the repository rather than an error.
set -uo pipefail

STATUSES=(ready in-progress blocked complete)
assignee=${ISSUE_ASSIGNEE-searledan}
repo='repos/{owner}/{repo}'

usage() {
	echo "usage: issue-status.sh <issue> <$(IFS='|'; echo "${STATUSES[*]}")>" >&2
	exit 64
}
die() { echo "issue-status: $*" >&2; exit 1; }

[ $# -eq 2 ] || usage
issue=$1 status=$2
[[ "$issue" =~ ^[1-9][0-9]*$ ]] || { echo "issue-status: '$issue' is not an issue number" >&2; usage; }
valid=0
for s in "${STATUSES[@]}"; do [ "$s" = "$status" ] && valid=1; done
[ "$valid" = 1 ] || { echo "issue-status: unknown status '$status'" >&2; usage; }
if [ -n "$assignee" ] && ! [[ "$assignee" =~ ^[A-Za-z0-9-]+$ ]]; then
	echo "issue-status: ISSUE_ASSIGNEE '$assignee' is not a GitHub login" >&2
	usage
fi
command -v jq >/dev/null || die "jq is required"

target="status: $status"
enc() { jq -rn --arg s "$1" '$s | @uri'; }

# The label must already exist in the repository (see the header).
gh api "$repo/labels/$(enc "$target")" --jq .name >/dev/null \
	|| die "the label '$target' does not exist in this repository; refusing to create it by adding it"

current=$(gh api "$repo/issues/$issue" --jq '[.labels[].name | select(startswith("status: "))] | .[]') \
	|| die "could not read issue #$issue"

gh api -X POST "$repo/issues/$issue/labels" -f "labels[]=$target" --jq '.[].name' >/dev/null \
	|| die "could not add '$target' to #$issue"

while IFS= read -r name; do
	[ -z "$name" ] || [ "$name" = "$target" ] && continue
	gh api -X DELETE "$repo/issues/$issue/labels/$(enc "$name")" --jq '.[].name' >/dev/null \
		|| die "added '$target' but could not remove '$name' from #$issue; it now has both -- re-run"
done <<< "$current"

if [ -n "$assignee" ]; then
	if [ "$status" = in-progress ]; then
		gh api -X POST "$repo/issues/$issue/assignees" -f "assignees[]=$assignee" --jq .number >/dev/null \
			|| die "set '$target' but could not assign $assignee to #$issue"
	else
		gh api -X DELETE "$repo/issues/$issue/assignees" -f "assignees[]=$assignee" --jq .number >/dev/null \
			|| die "set '$target' but could not unassign $assignee from #$issue"
	fi
fi

# Read back rather than trusting the calls: the result is what the issue now says.
after=$(gh api "$repo/issues/$issue" \
	--jq '"\([.labels[].name | select(startswith("status: "))] | sort | join(","))|\([.assignees[].login] | join(","))"') \
	|| die "updated #$issue but could not read it back to confirm"
labels_after=${after%%|*} assignees_after=${after#*|}

[ "$labels_after" = "$target" ] \
	|| die "#$issue reads back with status label(s) '$labels_after', not '$target'"
if [ -n "$assignee" ]; then
	on=0
	IFS=, read -ra logins <<< "$assignees_after"
	for l in "${logins[@]}"; do [ "${l,,}" = "${assignee,,}" ] && on=1; done
	if [ "$status" = in-progress ] && [ "$on" = 0 ]; then
		die "#$issue reads back without $assignee assigned"
	elif [ "$status" != in-progress ] && [ "$on" = 1 ]; then
		die "#$issue reads back with $assignee still assigned"
	fi
fi

echo "#$issue -> $target (assignees: ${assignees_after:-none})"

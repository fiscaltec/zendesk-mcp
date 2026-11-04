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
# The assignee is $ISSUE_ASSIGNEE, default `searledan` (the maintainer, who asked on 2026-11-04 to
# be assigned to an issue only while it is actively worked). ISSUE_ASSIGNEE= (empty) leaves assignees alone.
# The repository is gh's `{owner}/{repo}`: GH_REPO if set, else the checkout's default repository
# (`gh repo set-default`, else its remotes -- `upstream` before `origin`). In a fork with an
# `upstream` remote that is the upstream repository; set GH_REPO=owner/name to be explicit.
#
# Status labels are matched case-insensitively ("Status: Blocked" counts as a status label), and the
# read-back verdict is computed in jq, so the script runs on macOS's bash 3.2 as well as bash 5.
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
command -v gh >/dev/null || die "gh is required"

target="status: $status"
enc() { jq -rn --arg s "$1" '$s | @uri'; }
# Status labels of an issue, lower-cased prefix match; one per line.
STATUS_LABELS='.labels[].name | select(ascii_downcase | startswith("status: "))'

# Name the repository first: it proves the repository is visible, so a 404 on the label below
# means the label is missing, and every message can say which repository was reached.
nwo=$(gh api "$repo" --jq .full_name) && [ -n "$nwo" ] \
	|| die "could not read the repository gh resolved for {owner}/{repo} (set GH_REPO=owner/name to choose it)"

# The label must already exist, spelt exactly so (see the header). GitHub's lookup is
# case-insensitive, so compare the name it returns. "Could not check" is never reported as "missing".
# stderr goes to a file, so a notice gh prints on a successful call cannot pollute the name.
errf=$(mktemp) || die "could not create a temporary file"
trap 'rm -f "$errf"' EXIT
if ! got=$(gh api "$repo/labels/$(enc "$target")" --jq .name 2>"$errf"); then
	if grep -q "HTTP 404" "$errf"; then
		die "the label '$target' does not exist in $nwo; refusing to add it, which would create it"
	fi
	die "could not check whether the label '$target' exists in $nwo: $(tr '\n' ' ' < "$errf")"
fi
[ "$got" = "$target" ] || die "$nwo's label is spelt '$got', not '$target'; refusing to continue"

current=$(gh api "$repo/issues/$issue" --jq "$STATUS_LABELS") \
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

# Read back rather than trusting the calls (GitHub answers 201 to an assign it silently dropped).
# The whole verdict is one jq expression over the issue JSON: "ok <assignees>" or the reason.
issue_json=$(gh api "$repo/issues/$issue") \
	|| die "updated #$issue but could not read it back to confirm"
verdict=$(jq -r --arg t "$target" --arg a "$assignee" --arg s "$status" '
	[ '"$STATUS_LABELS"' ] as $labels
	| [ .assignees[].login ] as $logins
	| ($logins | map(ascii_downcase) | index($a | ascii_downcase) != null) as $on
	| if $labels != [$t] then
		"reads back with status label(s) \($labels | map("\u0027" + . + "\u0027") | join(" and ") | if . == "" then "none" else . end), not \u0027\($t)\u0027"
	  elif $a != "" and $s == "in-progress" and ($on | not) then "reads back without \($a) assigned"
	  elif $a != "" and $s != "in-progress" and $on then "reads back with \($a) still assigned"
	  else "ok \($logins | join(",") | if . == "" then "none" else . end)"
	  end' <<< "$issue_json") \
	|| die "updated #$issue but could not interpret the read-back"
case "$verdict" in
ok\ *) echo "$nwo#$issue -> $target (assignees: ${verdict#ok })" ;;
*) die "#$issue $verdict" ;;
esac

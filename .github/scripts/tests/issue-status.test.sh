#!/usr/bin/env bash
#
# Tests for .claude/scripts/issue-status.sh, run against a stateful stub `gh`
# (tests/fake-gh-issues) so the failure shapes a live repository rarely shows can be produced on
# demand: a refused call part-way through, a label the repository lacks, and a write that reports
# success but did not land.
#
# Run:  bash .github/scripts/tests/issue-status.test.sh
#
# Exit codes asserted, as documented in the script: 0 done and read back; 1 failed or the read-back
# disagrees; 64 usage error with no API call made.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/../../../.claude/scripts/issue-status.sh"

pass=0
fail=0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ALL_STATUSES=$'status: ready\nstatus: in-progress\nstatus: blocked\nstatus: complete'

# new_case <issue labels, newline-separated> <assignees, newline-separated>
new_case() {
	CASE="$WORK/case$((pass + fail + 1))"
	mkdir -p "$CASE/state" "$CASE/bin"
	cp "$HERE/fake-gh-issues" "$CASE/bin/gh"
	chmod +x "$CASE/bin/gh"
	export FAKE_GH_DIR="$CASE/state"
	printf '%s\n' "$ALL_STATUSES" tech-debt 'priority: medium' > "$FAKE_GH_DIR/repo-labels"
	printf '%s\n' "$1" > "$FAKE_GH_DIR/labels"
	printf '%s\n' "$2" > "$FAKE_GH_DIR/assignees"
	unset ISSUE_ASSIGNEE
}

run() {
	OUT="$(PATH="$CASE/bin:$PATH" bash "$SCRIPT" "$@" 2>&1)"
	RC=$?
}

labels() { grep -v '^$' "$FAKE_GH_DIR/labels" | sort | paste -sd, -; }
assignees() { grep -v '^$' "$FAKE_GH_DIR/assignees" | sort | paste -sd, -; }
calls() { cat "$FAKE_GH_DIR/calls.log" 2>/dev/null; }

check() { # $1 = description, then a condition evaluated with eval
	local desc="$1"; shift
	if eval "$@"; then
		pass=$((pass + 1))
	else
		fail=$((fail + 1))
		printf 'FAIL: %s\n  condition: %s\n  rc=%s\n  output:\n%s\n  labels: %s\n  assignees: %s\n  calls:\n%s\n\n' \
			"$desc" "$*" "$RC" "$(sed 's/^/    /' <<< "$OUT")" "$(labels)" "$(assignees)" "$(calls | sed 's/^/    /')"
	fi
}

# ---------------------------------------------------------------- the pass paths

new_case $'tech-debt\npriority: medium\nstatus: ready' ''
run 11 in-progress
check "ready -> in-progress exits 0" '[ "$RC" = 0 ]'
check "ready -> in-progress leaves exactly the new status label" '[ "$(labels)" = "priority: medium,status: in-progress,tech-debt" ]'
check "in-progress assigns searledan by default" '[ "$(assignees)" = searledan ]'
check "the label is added before the old one is removed" \
	'[ "$(calls | grep -nE "^(POST .*/labels|DELETE .*/labels/)" | cut -d: -f2 | cut -c1-4 | paste -sd, -)" = "POST,DELE" ]'
check "the old label is removed URL-encoded" 'calls | grep -qF "DELETE repos/{owner}/{repo}/issues/11/labels/status%3A%20ready"'

new_case $'bug\nstatus: in-progress' $'searledan\nsomeone-else'
run 11 complete
check "in-progress -> complete exits 0" '[ "$RC" = 0 ]'
check "in-progress -> complete leaves exactly status: complete" '[ "$(labels)" = "bug,status: complete" ]'
check "complete unassigns searledan and leaves other assignees alone" '[ "$(assignees)" = someone-else ]'

for s in ready blocked; do
	new_case $'bug\nstatus: in-progress' 'searledan'
	run 11 "$s"
	check "in-progress -> $s exits 0 and unassigns" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: $s" ] && [ -z "$(assignees)" ]'
done

new_case $'bug\nstatus: ready\nstatus: blocked' ''
run 11 in-progress
check "two stale status labels are both removed" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: in-progress" ]'

new_case 'bug' ''
run 11 ready
check "an issue with no status label gets one" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: ready" ]'

new_case $'bug\nstatus: in-progress' 'searledan'
run 11 in-progress
check "re-running the current status is idempotent" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: in-progress" ] && [ "$(assignees)" = searledan ]'
check "an idempotent re-run deletes nothing" '! calls | grep -q "^DELETE .*/labels/"'

new_case $'bug\nstatus: ready' ''
ISSUE_ASSIGNEE=someone-else run 11 in-progress
check "ISSUE_ASSIGNEE overrides the default" '[ "$RC" = 0 ] && [ "$(assignees)" = someone-else ]'

new_case $'bug\nstatus: ready' 'searledan'
ISSUE_ASSIGNEE= run 11 blocked
check "ISSUE_ASSIGNEE= (empty) leaves assignees alone" '[ "$RC" = 0 ] && [ "$(assignees)" = searledan ]'
check "ISSUE_ASSIGNEE= makes no assignee call" '! calls | grep -q assignees'

# ---------------------------------------------------------------- usage errors: no API call at all

for args in "" "11" "11 done" "11 In-Progress" "abc ready" "0 ready" "11 ready extra"; do
	new_case $'bug\nstatus: ready' ''
	# shellcheck disable=SC2086
	run $args
	check "usage error for '$args' exits 64" '[ "$RC" = 64 ]'
	check "usage error for '$args' makes no API call" '[ -z "$(calls)" ]'
done

new_case $'bug\nstatus: ready' ''
ISSUE_ASSIGNEE='bad;login' run 11 in-progress
check "a malformed ISSUE_ASSIGNEE is a usage error with no call" '[ "$RC" = 64 ] && [ -z "$(calls)" ]'

# ---------------------------------------------------------------- negative controls: fail, never pass

# The target label is missing from the repository: adding it would silently create it.
new_case $'bug\nstatus: ready' ''
grep -vxF 'status: blocked' "$FAKE_GH_DIR/repo-labels" > "$FAKE_GH_DIR/x" && mv "$FAKE_GH_DIR/x" "$FAKE_GH_DIR/repo-labels"
run 11 blocked
check "a label the repository lacks fails, saying it does not exist" '[ "$RC" = 1 ] && grep -q "does not exist" <<< "$OUT"'
check "a label the repository lacks is never added (would create it)" '! calls | grep -q "^POST"'
check "a label the repository lacks leaves the issue untouched" '[ "$(labels)" = "bug,status: ready" ]'

# The issue cannot be read.
new_case $'bug\nstatus: ready' ''
echo 'GET repos/{owner}/{repo}/issues/11' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "an unreadable issue fails before writing" '[ "$RC" = 1 ] && ! calls | grep -q "^POST"'

# The add is refused: the old status must survive (add-before-remove).
new_case $'bug\nstatus: ready' ''
echo 'POST repos/{owner}/{repo}/issues/11/labels' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a refused add fails" '[ "$RC" = 1 ]'
check "a refused add removes nothing, so the issue keeps a status" '[ "$(labels)" = "bug,status: ready" ]'

# The removal is refused: the issue has both labels, and the script says so.
new_case $'bug\nstatus: ready' ''
echo 'DELETE repos/{owner}/{repo}/issues/11/labels/' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a refused removal fails" '[ "$RC" = 1 ]'
check "a refused removal names both labels" 'grep -q "status: in-progress" <<< "$OUT" && grep -q "status: ready" <<< "$OUT"'

# The assign is refused.
new_case $'bug\nstatus: ready' ''
echo 'POST repos/{owner}/{repo}/issues/11/assignees' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a refused assign fails" '[ "$RC" = 1 ] && grep -q "could not assign" <<< "$OUT"'

# The unassign is refused.
new_case $'bug\nstatus: in-progress' 'searledan'
echo 'DELETE repos/{owner}/{repo}/issues/11/assignees' > "$FAKE_GH_DIR/fail"
run 11 complete
check "a refused unassign fails" '[ "$RC" = 1 ] && grep -q "could not unassign" <<< "$OUT"'

# Every call reports success but the add never lands: only the read-back can catch it.
new_case $'bug\nstatus: ready' ''
touch "$FAKE_GH_DIR/lose-adds"
run 11 in-progress
check "a write that did not land fails on read-back" '[ "$RC" = 1 ] && grep -q "reads back" <<< "$OUT"'

# The read-back itself fails: success must not be assumed.
new_case $'bug\nstatus: ready' ''
cat > "$CASE/bin/gh" <<EOF
#!/usr/bin/env bash
# Fails the read-back (the only read of the issue without --jq) and defers everything else to the stub.
if [ "\$*" = "api repos/{owner}/{repo}/issues/11" ]; then
  echo "gh: injected read-back failure" >&2; exit 1
fi
exec "$HERE/fake-gh-issues" "\$@"
EOF
chmod +x "$CASE/bin/gh"
run 11 in-progress
check "a failed read-back fails rather than reporting success" '[ "$RC" = 1 ] && grep -q "could not read it back" <<< "$OUT"'

# GitHub answers 201 to an assign it silently dropped: only the read-back can catch it.
new_case $'bug\nstatus: ready' ''
touch "$FAKE_GH_DIR/lose-assigns"
run 11 in-progress
check "an assign that did not land fails on read-back" '[ "$RC" = 1 ] && grep -q "reads back without searledan assigned" <<< "$OUT"'

new_case $'bug\nstatus: in-progress' 'searledan'
touch "$FAKE_GH_DIR/lose-unassigns"
run 11 complete
check "an unassign that did not land fails on read-back" '[ "$RC" = 1 ] && grep -q "still assigned" <<< "$OUT"'

# Logins compare case-insensitively, as GitHub's do.
new_case $'bug\nstatus: in-progress' 'SearleDan'
run 11 complete
check "a differently-cased login is still unassigned" '[ "$RC" = 0 ] && [ -z "$(assignees)" ]'
new_case $'bug\nstatus: ready' 'SearleDan'
run 11 in-progress
check "a differently-cased login counts as assigned" '[ "$RC" = 0 ] && [ "$(assignees)" = SearleDan ]'

# A hand-added, differently-cased status label is still a status label: removed, not ignored.
new_case $'bug\nStatus: Blocked\nstatus: ready' ''
run 11 in-progress
check "a mixed-case status label is removed" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: in-progress" ]'
new_case $'bug\nStatus: Blocked\nstatus: ready' ''
echo 'DELETE repos/{owner}/{repo}/issues/11/labels/Status' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a mixed-case status label that cannot be removed fails" '[ "$RC" = 1 ]'

# The per-call message for a refused removal, not only the read-back's.
new_case $'bug\nstatus: ready' ''
echo 'DELETE repos/{owner}/{repo}/issues/11/labels/' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a refused removal says which label and to re-run" 'grep -q "could not remove .status: ready." <<< "$OUT" && grep -q "re-run" <<< "$OUT"'

# A failed label lookup is "could not check", never "does not exist".
new_case $'bug\nstatus: ready' ''
echo 'GET repos/{owner}/{repo}/labels/' > "$FAKE_GH_DIR/fail"
run 11 in-progress
check "a failed label lookup fails without claiming the label is missing" \
	'[ "$RC" = 1 ] && grep -q "could not check" <<< "$OUT" && ! grep -q "does not exist" <<< "$OUT" && ! calls | grep -q "^POST"'

# The repository's label exists but is spelt differently: GitHub's lookup would still find it.
new_case $'bug\nstatus: ready' ''
sed 's/^status: blocked$/Status: Blocked/' "$FAKE_GH_DIR/repo-labels" > "$FAKE_GH_DIR/x" && mv "$FAKE_GH_DIR/x" "$FAKE_GH_DIR/repo-labels"
run 11 blocked
check "a differently-spelt repository label is refused before any write" \
	'[ "$RC" = 1 ] && grep -q "spelt" <<< "$OUT" && ! calls | grep -q "^POST"'

# A notice gh prints on stderr during a successful call must not be mistaken for the label name.
new_case $'bug\nstatus: ready' ''
touch "$FAKE_GH_DIR/stderr-notice"
run 11 in-progress
check "a stderr notice on success does not break the label check" '[ "$RC" = 0 ] && [ "$(labels)" = "bug,status: in-progress" ]'

# The repository itself cannot be read: no claim about labels, and no write.
new_case $'bug\nstatus: ready' ''
touch "$FAKE_GH_DIR/no-repo"
run 11 in-progress
check "an unreadable repository fails before any write, without blaming the label" \
	'[ "$RC" = 1 ] && grep -q "could not read the repository" <<< "$OUT" && ! grep -q "does not exist" <<< "$OUT" && ! calls | grep -q "^POST"'

new_case $'bug\nstatus: ready' ''
run 11 in-progress
check "the result names the repository reached" 'grep -q "^example/repo#11 -> status: in-progress" <<< "$OUT"'

echo "issue-status: $pass passed, $fail failed"
[ "$fail" = 0 ]

#!/usr/bin/env bash
# PreToolUse (Bash) hook: refuse a `git push` that would update `main` or `master`, force or not.
# Changes reach main through a PR and a squash merge only (CLAUDE.md -> GitHub issues).
#
# Claude Code hands the hook its input as JSON on stdin (`.tool_input.command`, `.cwd`) and reads a
# refusal from the PreToolUse permissionDecision JSON, as pre-merge-copilot-gate.sh does. The inline
# guard this replaces read `$CLAUDE_TOOL_INPUT`, which Claude Code does not set, and exited 1, which
# does not block, so it never refused anything (#13).
#
# What counts as a push to main: any `git push` whose arguments name main or master as a
# destination (`main`, `+main`, `HEAD:main`, `x:refs/heads/main`, `:main`), `--all` or `--mirror`,
# or a push with no refspec while the branch being pushed from is main or master. A remote spelt
# `main` is refused too; over-matching is the safe direction. Quoted text that contains a push
# command (`git commit -m "...; git push origin main"`) is over-matched for the same reason.
#
# Fails closed: a payload that mentions `push` but cannot be read is refused. A payload that does
# not mention `push` at all is allowed before anything is parsed, so a broken `jq` cannot stop every
# other Bash call. Tested by .github/scripts/tests/guard-hooks.test.sh.
set -uo pipefail

deny() {
	local reason="BLOCKED: $1 Never push to main; open a PR from a feature branch instead."
	if printf '{}' | jq -e . >/dev/null 2>&1; then
		printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":%s}}\n' \
			"$(printf '%s' "$reason" | jq -Rs .)"
	else
		printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"BLOCKED: a git push could not be checked (jq is missing or not runnable). Never push to main."}}\n'
	fi
	exit 0
}

input=$(cat)
[[ "$input" == *push* ]] || exit 0

printf '{}' | jq -e . >/dev/null 2>&1 || deny "this push could not be checked: jq is missing or not runnable (failing closed)."
cmd=$(printf '%s' "$input" | jq -er '.tool_input.command' 2>/dev/null) \
	|| deny "this push could not be checked: the hook input has no readable command (failing closed)."
cwd=$(printf '%s' "$input" | jq -r '.cwd // empty' 2>/dev/null)
[ -n "$cwd" ] || cwd=$PWD

is_main() { [[ "$1" == main || "$1" == master ]]; }

# The branch a refspec-less push would push: the one checked out in $1.
current_branch() { git -C "$1" symbolic-ref --quiet --short HEAD 2>/dev/null; }

# Split into simple commands on ; & | and newlines (&& and || included).
segments=$(printf '%s\n' "$cmd" | tr ';&|' '\n\n\n')

dir=$cwd
while IFS= read -r segment; do
	# Strip quotes rather than parse them: a quoted argument is then just its words.
	segment=${segment//\"/}
	segment=${segment//\'/}
	read -ra words <<<"$segment"
	[ "${#words[@]}" -gt 0 ] || continue

	# Track `cd <dir>` so a later refspec-less push is checked in the right repository.
	if [ "${words[0]}" = cd ] && [ "${#words[@]}" -ge 2 ]; then
		case "${words[1]}" in
		/*) dir=${words[1]} ;;
		*) dir=$dir/${words[1]} ;;
		esac
		continue
	fi

	# Find `git`, then its subcommand: the first word that is not a global option or its value.
	i=0
	while [ "$i" -lt "${#words[@]}" ] && [ "${words[$i]}" != git ]; do i=$((i + 1)); done
	[ "$i" -lt "${#words[@]}" ] || continue
	i=$((i + 1))
	gitdir=$dir
	while [ "$i" -lt "${#words[@]}" ]; do
		case "${words[$i]}" in
		-C)
			i=$((i + 1))
			case "${words[$i]:-}" in
			/*) gitdir=${words[$i]} ;;
			*) gitdir=$gitdir/${words[$i]:-} ;;
			esac
			;;
		-c | --git-dir | --work-tree | --namespace) i=$((i + 1)) ;;
		-*) ;;
		*) break ;;
		esac
		i=$((i + 1))
	done
	[ "${words[$i]:-}" = push ] || continue

	positionals=0
	for word in "${words[@]:$((i + 1))}"; do
		case "$word" in
		--all | --mirror) deny "\`git push $word\` updates main too." ;;
		-*) continue ;;
		esac
		positionals=$((positionals + 1))
		dest=${word#+}
		dest=${dest##*:}
		dest=${dest#refs/heads/}
		# `HEAD` (`git push origin HEAD`) is whatever branch is checked out.
		[ "$dest" = HEAD ] && dest=$(current_branch "$gitdir")
		is_main "$dest" && deny "This push updates $dest."
	done

	# No refspec (`git push`, `git push origin`): the current branch is what gets pushed.
	if [ "$positionals" -le 1 ]; then
		branch=$(current_branch "$gitdir")
		[ -n "$branch" ] && is_main "$branch" \
			&& deny "This push has no refspec and $branch is checked out, so it would push $branch."
	fi
done <<<"$segments"

exit 0

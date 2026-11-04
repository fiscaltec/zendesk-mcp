#!/usr/bin/env bash
# PreToolUse (Bash) hook: refuse a `git push` that would update `main` or `master`, force or not.
# Changes reach main through a PR and a squash merge only (CLAUDE.md -> GitHub issues).
#
# Claude Code hands the hook its input as JSON on stdin (`.tool_input.command`, `.cwd`) and reads a
# refusal from the PreToolUse permissionDecision JSON, as pre-merge-copilot-gate.sh does. The inline
# guard this replaces read `$CLAUDE_TOOL_INPUT`, which Claude Code does not set, and exited 1, which
# does not block, so it never refused anything (#13).
#
# A push is refused when:
#   * an argument names main or master as a destination (`main`, `+main`, `HEAD:main`,
#     `x:refs/heads/main`, `:main`); a remote spelt `main` is refused too;
#   * it uses `--all`, `--mirror`, a matching refspec (`:`) or a wildcard (`*`);
#   * its destination is implicit (no refspec, or `HEAD`) and resolves to main or master: the
#     branch checked out, or its upstream under `push.default=upstream`; or it cannot be resolved,
#     because a `-c` option or a `remote.*.push` setting is in play, `push.default` is `matching`,
#     or an earlier command in the same line changes branch (`git checkout main && git push`);
#   * it cannot be read with certainty: a quoted argument containing whitespace or ; & |,
#     unbalanced quotes, `$` or a backtick (a variable or substitution could be main).
#
# Quoted text elsewhere in the line is collapsed before the line is split, so a commit message
# such as `git commit -m "...; git push origin main"` is not mistaken for a push.
#
# This is a guard against mistakes, not a sandbox: a script that runs `git push` itself is out of
# its sight. The `Secure branches` ruleset on GitHub is what actually protects main.
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
unreadable() { deny "This push could not be checked with certainty ($1); write it with literal, unquoted arguments."; }

input=$(cat)
[[ "$input" == *push* ]] || exit 0

printf '{}' | jq -e . >/dev/null 2>&1 || deny "This push could not be checked: jq is missing or not runnable (failing closed)."
cmd=$(printf '%s' "$input" | jq -er '.tool_input.command' 2>/dev/null) \
	|| deny "This push could not be checked: the hook input has no readable command (failing closed)."
cwd=$(printf '%s' "$input" | jq -r '.cwd // empty' 2>/dev/null)
[ -n "$cwd" ] || cwd=$PWD

is_main() { [[ "$1" == main || "$1" == master || "$1" == refs/heads/main || "$1" == refs/heads/master ]]; }

# The branch checked out in $1.
current_branch() { git -C "$1" symbolic-ref --quiet --short HEAD 2>/dev/null; }

# Remove backslash-newline continuations, as the shell does (the pair goes, with no space in its
# place, so `ma\<newline>in` is `main`), before anything is split.
cmd=${cmd//$'\\\n'/}
# Drop redirections (`2>&1`, `>/tmp/x`, `> /tmp/x`, `<in`) so they are not read as arguments, and
# so the `&` in `2>&1` does not split a command in two.
cmd=$(printf '%s\n' "$cmd" | sed -E 's/[0-9]*>>?&[0-9-]+//g; s/[0-9]*(>>?|<)[[:space:]]*[^[:space:];&|]+//g')
# Collapse each quoted string to one word before splitting, so quoted ; & | and spaces do not
# split it: its content if that is a single plain word, otherwise a placeholder that a push
# refuses as unreadable (`git -C "/path with space" push` must not lose its push).
QUOTED=__quoted_text__
QUOTE_RE='^([^"'"'"']*)("[^"]*"|'"'"'[^'"'"']*'"'"')(.*)$'
SPLITS_RE='[[:space:];&|]'
collapsed=""
rest=$cmd
while [[ "$rest" =~ $QUOTE_RE ]]; do
	before=${BASH_REMATCH[1]} quoted=${BASH_REMATCH[2]} rest=${BASH_REMATCH[3]}
	inner=${quoted:1:${#quoted}-2}
	[[ "$inner" =~ $SPLITS_RE || -z "$inner" ]] && inner=$QUOTED
	collapsed+=$before$inner
done
cmd=$collapsed$rest
# Split into simple commands on ; & | and newlines (&& and || included).
segments=$(printf '%s\n' "$cmd" | tr ';&|' '\n\n\n')

dir=$cwd
branch_changed=""
while IFS= read -r segment; do
	raw=$segment
	# Subshell and group brackets are not words of the command: `(git push origin main)`.
	segment=${segment//[()]/ }
	read -ra words <<<"$segment"
	for k in "${!words[@]}"; do [[ "${words[$k]}" == [{}] ]] && unset 'words[k]'; done
	words=("${words[@]}")
	[ "${#words[@]}" -gt 0 ] || continue

	# Track `cd <dir>` so a later implicit push is checked in the right repository.
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
	configured=""
	selector=""
	while [ "$i" -lt "${#words[@]}" ]; do
		case "${words[$i]}" in
		-C)
			i=$((i + 1))
			case "${words[$i]:-}" in
			/*) gitdir=${words[$i]} ;;
			*) gitdir=$gitdir/${words[$i]:-} ;;
			esac
			;;
		-C?*)
			case "${words[$i]#-C}" in
			/*) gitdir=${words[$i]#-C} ;;
			*) gitdir=$gitdir/${words[$i]#-C} ;;
			esac
			;;
		-c) configured=1; i=$((i + 1)) ;;
		-c* | --config-env*) configured=1 ;;
		# These pick another repository in ways this hook does not follow.
		--git-dir | --work-tree) selector=${words[$i]}; i=$((i + 1)) ;;
		--git-dir=* | --work-tree=*) selector=${words[$i]%%=*} ;;
		--namespace) i=$((i + 1)) ;;
		-*) ;;
		*) break ;;
		esac
		i=$((i + 1))
	done
	sub=${words[$i]:-}

	# Remember a branch change earlier in the line: an implicit push after it would see a
	# different branch from the one this hook can look at now.
	case "$sub" in
	checkout | switch | symbolic-ref | rebase | worktree | update-ref) branch_changed=$sub ;;
	branch) [[ " ${words[*]:$((i + 1))} " =~ [[:space:]]-(m|M|c|C|-move|-copy|f|-force)[[:space:]] ]] && branch_changed=branch ;;
	esac
	[ "$sub" = push ] || continue

	# The push's own arguments, read only where their boundaries are certain.
	[[ "$raw" == *'$'* || "$raw" == *'`'* ]] && unreadable "it uses \$ or a backtick"
	[[ "$raw" == *"$QUOTED"* ]] && unreadable "a quoted argument contains whitespace or ; & |"
	[[ "$raw" == *[\"\']* ]] && unreadable "its quotes are unbalanced"

	positionals=()
	args=("${words[@]:$((i + 1))}")
	j=0
	while [ "$j" -lt "${#args[@]}" ]; do
		word=${args[$j]}
		case "$word" in
		--all | --mirror) deny "\`git push $word\` updates main too." ;;
		-o | --push-option | --repo | --receive-pack | --exec) j=$((j + 1)) ;;
		-*) ;;
		*) positionals+=("$word") ;;
		esac
		j=$((j + 1))
	done

	implicit=""
	[ "${#positionals[@]}" -le 1 ] && implicit=1
	for word in "${positionals[@]:1}"; do
		[[ "$word" == *'*'* ]] && deny "Wildcard refspecs can include main."
		[ "${word#+}" = : ] && deny "A matching refspec (\`:\`) pushes main if it exists on both sides."
		dest=${word#+}
		dest=${dest##*:}
		if [ "$dest" = HEAD ]; then
			implicit=1
		else
			is_main "$dest" && deny "This push updates $dest."
		fi
	done
	# The remote itself (a remote spelt main is refused too; over-matching is the safe direction).
	[ "${#positionals[@]}" -ge 1 ] && is_main "${positionals[0]##*:}" && deny "This push names ${positionals[0]}."

	[ -n "$implicit" ] || continue
	# The destination comes from the checkout and configuration: resolve it, or refuse.
	[ -n "$branch_changed" ] \
		&& deny "An earlier \`git $branch_changed\` in this command can change the branch this push sends; name the destination branch explicitly."
	[ -n "$selector" ] && deny "\`$selector\` points this push at another repository, whose branch this hook cannot check; name the destination branch explicitly."
	[ -n "$configured" ] && deny "A \`-c\` option can change where a push without an explicit destination goes."
	git -C "$gitdir" config --get-regexp '^remote\..*\.push$' >/dev/null 2>&1 \
		&& deny "This repository sets remote.*.push, so a push without an explicit destination may update main."
	branch=$(current_branch "$gitdir")
	[ -n "$branch" ] || continue
	is_main "$branch" && deny "This push has no explicit destination and $branch is checked out, so it would push $branch."
	mode=$(git -C "$gitdir" config --get push.default 2>/dev/null)
	case "$mode" in
	matching) deny "push.default is matching, so this push updates every branch that exists on both sides, main included." ;;
	upstream | tracking)
		upstream=$(git -C "$gitdir" config --get "branch.$branch.merge" 2>/dev/null)
		is_main "$upstream" && deny "push.default is $mode and $branch tracks $upstream, so this push would update it."
		;;
	esac
done <<<"$segments"

exit 0

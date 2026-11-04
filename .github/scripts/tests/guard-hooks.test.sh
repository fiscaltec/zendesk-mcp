#!/usr/bin/env bash
#
# Tests for .claude/hooks/guard-push-to-main.sh and .claude/hooks/guard-secret-files.sh, fed the
# JSON Claude Code writes to a PreToolUse hook's stdin. Both hooks allow by printing nothing and
# deny by printing PreToolUse JSON; they exit 0 either way.
#
# Run:  bash .github/scripts/tests/guard-hooks.test.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUSH_HOOK="$HERE/../../../.claude/hooks/guard-push-to-main.sh"
FILE_HOOK="$HERE/../../../.claude/hooks/guard-secret-files.sh"

pass=0
fail=0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Two throwaway repositories: one on main, one on a feature branch, for refspec-less pushes.
for b in main feat/thing; do
	d="$WORK/repo-${b//\//-}"
	git init -q -b "$b" "$d"
done
ON_MAIN="$WORK/repo-main"
ON_FEATURE="$WORK/repo-feat-thing"
# A repository on main whose path contains a space, and feature checkouts whose configuration
# decides where a push without an explicit destination goes.
git init -q -b main "$WORK/repo main"
mk() { # $1 = name, then git config pairs
	local d="$WORK/$1"
	shift
	git init -q -b feat/thing "$d"
	while [ "$#" -gt 0 ]; do git -C "$d" config "$1" "$2"; shift 2; done
	printf '%s' "$d"
}
CONFIGURED_PUSH=$(mk configured-push remote.origin.push HEAD:main)
MATCHING=$(mk matching push.default matching)
UPSTREAM_MAIN=$(mk upstream-main push.default upstream branch.feat/thing.merge refs/heads/main)
UPSTREAM_FEATURE=$(mk upstream-feature push.default upstream branch.feat/thing.merge refs/heads/feat/thing)

check() { # $1 = allow|deny, $2 = name, $3 = hook, $4 = payload
	local out decision
	out=$(printf '%s' "$4" | bash "$3" 2>/dev/null)
	if [ -z "$out" ]; then
		decision=allow
	elif [ "$(printf '%s' "$out" | jq -r '.hookSpecificOutput.permissionDecision' 2>/dev/null)" = deny ]; then
		decision=deny
	else
		decision="unreadable output: $out"
	fi
	if [ "$decision" = "$1" ]; then
		pass=$((pass + 1))
	else
		fail=$((fail + 1))
		echo "FAIL: $2 — expected $1, got $decision"
	fi
}

bash_payload() { # $1 = command, $2 = cwd
	jq -nc --arg c "$1" --arg d "$2" '{hook_event_name:"PreToolUse", tool_name:"Bash", cwd:$d, tool_input:{command:$c}}'
}
push() { check "$1" "push: $2" "$PUSH_HOOK" "$(bash_payload "$2" "${3:-$ON_FEATURE}")"; }

# Refused: main or master named as a destination, however it is spelt.
push deny 'git push origin main'
push deny 'git push -u origin main'
push deny 'git push origin master'
push deny 'git push --force origin main'
push deny 'git push -f origin main'
push deny 'git push --force-with-lease origin main'
push deny 'git push origin +main'
push deny 'git push origin HEAD:main'
push deny 'git push origin HEAD:refs/heads/main'
push deny 'git push origin fix/thing:main'
push deny 'git push origin :main'
push deny 'git push origin fix/a main'
push deny 'git push --all origin'
push deny 'git push --mirror'
push deny 'git -C /somewhere push origin main'
push deny 'git -c push.default=current push origin main'
push deny 'cd /tmp && git push origin main'
push deny 'git fetch origin; git push origin main'
push deny 'git status && git push origin main 2>&1 | tail -2'
push deny 'timeout 90 git push -q -u origin main'
push deny 'git push "origin" "main"'

# Refused: no refspec, or HEAD, while main is checked out.
push deny 'git push' "$ON_MAIN"
push deny 'git push origin' "$ON_MAIN"
push deny 'git push -u origin HEAD' "$ON_MAIN"
push deny "cd $ON_MAIN && git push" "$ON_FEATURE"
push deny "git -C $ON_MAIN push" "$ON_FEATURE"

# Refused: the bypasses Copilot found on PR #47.
push deny $'git push origin \\\nmain'
push deny "git -C \"$WORK/repo main\" push origin HEAD"
push deny 'git push -o ci.skip origin' "$ON_MAIN"
push deny 'git push origin > /tmp/push.log' "$ON_MAIN"
push deny 'git push origin :'
push deny "git push origin 'refs/heads/*:refs/heads/*'"
push deny 'git -c remote.origin.push=HEAD:main push origin'
push deny 'git checkout main && git push origin'
push deny 'git switch main; git push'
push deny 'git checkout main && git push -u origin HEAD'
# Refused: the bypasses Copilot found in its second pass on PR #47.
push deny $'git push origin ma\\\nin'
push deny '(git push origin main)'
push deny '{ git push origin main; }'
push deny "git --git-dir $ON_MAIN/.git push origin"
push deny "git --git-dir=$ON_MAIN/.git push origin"
push deny "git --work-tree $ON_MAIN push"
push deny "git -C$ON_MAIN push origin"
push allow "git -C$ON_FEATURE push origin"
push allow '(cd /tmp && git push origin fix/thing)'
push allow "git --git-dir $ON_MAIN/.git push origin fix/thing"
# Refused: implicit destinations the configuration sends to main, or that cannot be read.
push deny 'git push' "$CONFIGURED_PUSH"
push deny 'git push' "$MATCHING"
push deny 'git push origin' "$UPSTREAM_MAIN"
push deny 'git push origin $BRANCH'
push deny 'git push origin $(git branch --show-current)'
push deny 'git push origin "fix/a'

# Refused: what the Claude review found (bash 3.2, unresolvable directories, heredocs, spellings).
push deny 'git status && git push origin main'
push deny 'git status || git push origin main'
push deny 'cd ~/somewhere && git push'
push deny 'cd $OLDPWD && git push'
push deny "pushd $ON_MAIN && git push"
push deny "GIT_DIR=$ON_MAIN/.git git push origin"
push deny 'git push' /nonexistent
push deny $'git commit -F - <<EOF\nfix: don\'t break\nEOF\ngit push origin main && echo \'pushed\''
push deny 'git pu\sh origin main'
push deny 'git push origin ma\in'
push deny "git p''ush origin main"
push deny '/usr/bin/git push origin main'
push deny '\git push origin main'
push deny 'git push origin {main,x}'
push deny 'git push origin HEAD:heads/main'
push deny 'git -c alias.p=push p origin main'
push allow "pushd $ON_FEATURE && git push"
push allow 'git status && git push -u origin fix/thing'
push allow $'git commit -F - <<EOF\nfix: a message\nEOF\ngit push -u origin fix/thing'

# Allowed: everyday pushes from a feature branch, redirected, piped and chained.
push allow 'git push -u origin fix/thing 2>&1 | tail -2'
push allow 'timeout 90 git push -q -u origin fix/thing 2>&1 | grep -v remote'
push allow 'git add -A && git commit -m "a message; git push origin main" && git push -u origin fix/thing'
push allow 'git push -o ci.skip origin fix/thing'
push allow 'git push origin "fix/thing"'
push allow 'git checkout -b fix/new && git push -u origin fix/new'
push allow 'git push origin' "$UPSTREAM_FEATURE"

# Allowed: feature branches, including names that merely contain "main".
push allow 'git push -u origin fix/thing'
push allow 'git push origin review/main-snapshot'
push allow 'git push origin fix/main-menu'
push allow 'git push origin main:fix/thing'
push allow 'git push' "$ON_FEATURE"
push allow 'git push origin' "$ON_FEATURE"
push allow 'git push -u origin HEAD' "$ON_FEATURE"
push allow "cd $ON_FEATURE && git push" "$ON_MAIN"

# Allowed: not a push at all, though the words appear.
push allow 'git log --oneline main'
push allow 'git checkout main'
push allow 'git commit -m "push the fix"'
push allow 'echo push'
push allow 'git stash push -m wip'
push allow 'ls'

# Fails closed on a payload that mentions push but cannot be read.
check deny "push: unreadable payload" "$PUSH_HOOK" 'not json, git push origin main'
check deny "push: payload with no command" "$PUSH_HOOK" '{"tool_input":{"description":"git push"}}'
# ...but never reads (or needs jq for) a payload that does not mention git.
check allow "push: unreadable payload without push" "$PUSH_HOOK" 'not json at all'

file_payload() { # $1 = tool, $2 = path
	jq -nc --arg t "$1" --arg p "$2" '{hook_event_name:"PreToolUse", tool_name:$t, tool_input:{file_path:$p, content:"x"}}'
}
file() { check "$1" "file: $2 $3" "$FILE_HOOK" "$(file_payload "$2" "$3")"; }

file deny Write /repo/ZendeskMcp/appsettings.json
file deny Edit /repo/ZendeskMcp/appsettings.json
file deny Write /repo/ZendeskMcp/appsettings.Development.json
file deny Write /repo/ZendeskMcp/appsettings.Production.json
file deny Write /repo/.env
file deny Write /repo/.env.local
file deny Write /repo/config/staging.env
file allow Write /repo/ZendeskMcp/appsettings.Example.json
file allow Edit /repo/README.md
file allow Write /repo/ZendeskMcp/Program.cs
check deny "file: notebook path" "$FILE_HOOK" '{"tool_name":"NotebookEdit","tool_input":{"notebook_path":"/repo/.env"}}'
check deny "file: unreadable payload" "$FILE_HOOK" 'not json'
check deny "file: payload with no path" "$FILE_HOOK" '{"tool_name":"Write","tool_input":{}}'

echo "guard hooks: $pass passed, $fail failed"
[ "$fail" -eq 0 ]

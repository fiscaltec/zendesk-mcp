#!/usr/bin/env bash
# PreToolUse (Edit|Write) hook: refuse to write a file that is likely to hold secrets. Configuration
# examples belong in appsettings.Example.json, and settings in CLAUDE.md as environment variables.
#
# Claude Code hands the hook its input as JSON on stdin (`.tool_input.file_path`, or
# `.tool_input.notebook_path` for a notebook) and reads a refusal from the PreToolUse
# permissionDecision JSON. The inline guard this replaces read `$CLAUDE_FILE_PATHS`, which Claude
# Code does not set, and exited 1, which does not block, so it never refused anything (#13).
#
# Fails closed: input that cannot be read is refused. Tested by
# .github/scripts/tests/guard-hooks.test.sh.
set -uo pipefail

deny() {
	printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"BLOCKED: %s"}}\n' "$1"
	exit 0
}

input=$(cat)
printf '{}' | jq -e . >/dev/null 2>&1 || deny "this write could not be checked: jq is missing or not runnable (failing closed)."
path=$(printf '%s' "$input" | jq -er '.tool_input.file_path // .tool_input.notebook_path' 2>/dev/null) \
	|| deny "this write could not be checked: the hook input names no file (failing closed)."

case "$path" in
*.env | *.env.* | *appsettings.json | *appsettings.Development.json | *appsettings.Production.json)
	deny "refusing to write to a likely-secret file. Use appsettings.Example.json or document the environment variable in CLAUDE.md instead."
	;;
esac

exit 0

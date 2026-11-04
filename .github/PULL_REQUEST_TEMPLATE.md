<!--
Title format: <type>: <short description>
Allowed types: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert
Examples:
  feat: add a tool to list ticket tags
  fix: handle 429 retries when Retry-After is absent
  docs: refresh README install instructions for Claude Code
-->

## Summary

<!-- 1-3 bullet points describing what changes and why. -->

-
-

## Breaking changes

<!-- Anything that requires action from existing users (env var changes,
     default behaviour shifts, removed tools, renamed config keys). Delete
     this section if there are none. -->

-

## Test plan

- [ ] `dotnet test ZendeskMcp.sln -c Release` passes locally
- [ ] CI is green on this PR
- [ ] Manual smoke test against staging (`https://zendesk-mcp-staging.fiscaltec.com/mcp`), connected
      as your own Zendesk account (note which tools you exercised):

## Documentation

- [ ] `CLAUDE.md` reflects the change (if applicable)
- [ ] `README.md` reflects the change (if applicable)

## Linked issues

<!-- e.g. Closes #123, Fixes #456 -->

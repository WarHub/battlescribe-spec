---
name: managing-backlog
description: >
  Triage, prioritize, and manage backlog issues. Use when creating new issues,
  grooming the backlog, re-prioritizing work, or analyzing issue status. Covers
  the label taxonomy, epic hierarchy, issue type conventions, and gh CLI patterns
  for label/sub-issue operations.
---

# Managing the Backlog

## Quick start: creating a new issue

1. **Draft the issue body** with Summary, Current Behavior, Desired Behavior, and Acceptance Criteria sections
2. **Choose issue type**: Epic, Feature, Task, or Bug (set via GraphQL — see below)
3. **Apply labels**: one `priority:` label + one or more `area:` labels + optional `needs-design`
4. **Set parent epic** if the issue belongs to one (via GraphQL `addSubIssue`)
5. **Update the parent epic's body** to list the new sub-issue

## Issue types

| Type | When to use |
|------|-------------|
| **Epic** | Top-level tracking issue with sub-issues. Goal + sub-issue list + current state. |
| **Feature** | A new capability requiring design. May spawn Tasks. |
| **Task** | A concrete, implementable piece of work. |
| **Bug** | Something broken that needs fixing. |

Set via `updateIssue` GraphQL mutation (see **gh-cli** skill for syntax).

WarHub org Type IDs:
| Type | ID |
|------|------|
| Task | `IT_kwDOAGQzxM4ACWa6` |
| Bug | `IT_kwDOAGQzxM4ACWa9` |
| Feature | `IT_kwDOAGQzxM4ACWbA` |
| Epic | `IT_kwDOAGQzxM4B4bYK` |

## Label taxonomy

### Priority labels (mutually exclusive — pick exactly one)

| Label | Color | Criteria |
|-------|-------|----------|
| `priority: high` | 🔴 red | Immediate value, low friction, or prevents growing problems |
| `priority: medium` | 🟠 orange | Good value, moderate effort, not urgent |
| `priority: low` | 🟡 yellow | Longer-term investment, lower urgency |
| `priority: backlog` | ⚪ light blue | Future work, scope undecided, blocked on design decisions |

### Area labels (one or more)

| Label | Scope |
|-------|-------|
| `area: spec-coverage` | Writing/expanding conformance specs |
| `area: framework` | Spec runner, loader, tooling, infrastructure |
| `area: devex` | Developer experience, refactoring, code quality |
| `area: newrecruit` | NewRecruit engine-specific |

### Status label

| Label | When |
|-------|------|
| `needs-design` | Requires a design decision before implementation can start |

## gh CLI gotcha: labels with colons

`gh issue edit --add-label` parses `label: name` as an issue reference format.
**Always use the REST API** to add labels containing `: `:

```powershell
$tempFile = Join-Path $env:TEMP "labels.json"
@{ labels = @("priority: high", "area: newrecruit") } |
  ConvertTo-Json | Set-Content $tempFile -Encoding utf8NoBOM
gh api "repos/WarHub/battlescribe-spec/issues/NUMBER/labels" `
  --method POST --input $tempFile
Remove-Item $tempFile
```

## Sub-issue management

Use `addSubIssue` / `removeSubIssue` GraphQL mutations or the REST sub-issues
API — see **gh-cli** skill (`issue-relationships.md`) for full syntax.

After linking, **update the parent's body** to list the new sub-issue.

After closing an issue with a parent, check if all siblings are closed too →
close the parent if so. See [QUERYING-ISSUES.md](references/QUERYING-ISSUES.md)
for the query.

## Triage workflow

When grooming a batch of issues:

1. **Inventory**: `gh issue list --repo WarHub/battlescribe-spec --state open --limit 100` — cross-check labels, types, and parent links via GraphQL (see [QUERYING-ISSUES.md](references/QUERYING-ISSUES.md))
2. **Classify**: Apply priority + area labels (REST API — see colon gotcha above), set issue type (GraphQL), link to parent epic
3. **Close completed**: Verify acceptance criteria met; check if parent epic can be closed too

## Cross-references

- **gh-cli** skill — GraphQL mutation syntax for issue types, sub-issues, blocking, and fields
- [QUERYING-ISSUES.md](references/QUERYING-ISSUES.md) — GraphQL queries for discovering epics, sub-issues, and backlog state
- [LABEL-TAXONOMY.md](references/LABEL-TAXONOMY.md) — Detailed label definitions and decision guide

## Self-Enhancement Triggers

After completing a triage or issue-management task, check:
1. Did I encounter an issue type or label not covered here? → Propose an update
2. Did a GraphQL query fail or return unexpected results? → Note in QUERYING-ISSUES.md
3. Did the user override my priority assessment? → Refine criteria in LABEL-TAXONOMY.md

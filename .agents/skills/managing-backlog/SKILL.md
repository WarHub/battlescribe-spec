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

Issue types are set via GraphQL (no `gh issue` flag exists):
```powershell
# Set type (get issue node ID first via: repository(owner,name) { issue(number) { id } })
gh api graphql -f query='mutation { updateIssue(input: {
  id: "ISSUE_NODE_ID", issueTypeId: "IT_xxx"
}) { issue { title issueType { name } } } }'
```

Type IDs (WarHub org):
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

Add a sub-issue to an epic (get both node IDs first via `repository { issue(number) { id } }`):
```powershell
gh api graphql -f query='mutation { addSubIssue(input: {
  issueId: "EPIC_NODE_ID", subIssueId: "CHILD_NODE_ID"
}) { issue { number } subIssue { number } } }'
```

After linking, **update the parent's body** to list the new sub-issue.

### Checking parent completion

After closing an issue that has a parent, check if all sibling sub-issues are
also closed. If so, close the parent too:
```powershell
gh api graphql -f query='query { repository(owner: "WarHub", name: "battlescribe-spec") {
  issue(number: PARENT_NUM) { subIssues(first: 100) { nodes { number state } } }
} }'
```

## Triage workflow

When grooming a batch of issues:

1. **List open issues**: `gh issue list --repo WarHub/battlescribe-spec --state open --limit 100`
2. **Check each issue's current labels and type** (see hierarchy reference)
3. **Assess priority** using label criteria (see taxonomy above and [LABEL-TAXONOMY.md](references/LABEL-TAXONOMY.md))
4. **Apply labels** via REST API
5. **Set/verify issue type** via GraphQL
6. **Link to parent epic** if applicable
7. **Close completed issues** — check if all acceptance criteria are met
8. **Check parent completion** — if a closed issue has a parent, check if all siblings are done too

## Reference files

- [QUERYING-ISSUES.md](references/QUERYING-ISSUES.md) — GraphQL queries to discover epics, sub-issues, and current backlog state
- [LABEL-TAXONOMY.md](references/LABEL-TAXONOMY.md) — Detailed label definitions and decision guide

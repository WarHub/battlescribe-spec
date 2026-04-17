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
# Get type IDs
gh api graphql -f query='query { organization(login: "WarHub") {
  issueTypes(first: 20) { nodes { id name } }
} }'

# Set type on an issue (get node ID first)
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

Add a sub-issue to an epic via GraphQL:
```powershell
# Get node IDs
gh api graphql -f query='query { repository(owner: "WarHub", name: "battlescribe-spec") {
  issue(number: EPIC_NUM) { id }
} }'
gh api graphql -f query='query { repository(owner: "WarHub", name: "battlescribe-spec") {
  issue(number: CHILD_NUM) { id }
} }'

# Link
gh api graphql -f query='mutation { addSubIssue(input: {
  issueId: "EPIC_NODE_ID", subIssueId: "CHILD_NODE_ID"
}) { issue { number } subIssue { number } } }'
```

After linking, **update the epic's body** to include the new sub-issue in its list.

## Triage workflow

When grooming a batch of issues:

1. **List open issues**: `gh issue list --repo WarHub/battlescribe-spec --state open --limit 100`
2. **Check each issue's current labels and type** (see hierarchy reference)
3. **Assess priority** using these criteria:
   - HIGH: Quick wins, coverage gaps, prevents accumulating problems
   - MEDIUM: Good value, clear scope, moderate effort
   - LOW: Large investment, needs research, not blocking
   - BACKLOG: Scope undecided, blocked on design, far-future
4. **Apply labels** via REST API
5. **Set/verify issue type** via GraphQL
6. **Link to parent epic** if applicable
7. **Close completed issues** — check if all acceptance criteria are met

## Reference files

- [ISSUE-HIERARCHY.md](references/ISSUE-HIERARCHY.md) — Full epic/sub-issue tree with status
- [LABEL-TAXONOMY.md](references/LABEL-TAXONOMY.md) — Detailed label definitions and decision guide

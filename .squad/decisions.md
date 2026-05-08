# Squad Decisions

## Active Decisions

### Decision: WarHub Project Board (v2) Structure & Team Integration

**Author:** Avasarala (Lead)  
**Date:** 2026-05-08  
**Status:** Active  
**Scope:** Planning integration, backlog management, team coordination

#### Context

WarHub/battlescribe-spec uses GitHub Project Board v2 (#2) to track conformance spec work. Amadeusz requested a complete analysis of board structure and backlog to enable effective team planning and workflow management.

#### Current Project Board Status

**Issue:** Current gh authentication token lacks `read:project` scope. Cannot fully query project fields, views, or item status directly via CLI. This must be remedied for Copilot CLI automation.

**Recommendation:** Regenerate/refresh gh token with explicit `read:project` scope in personal token settings (Settings > Developer Settings > Personal access tokens). Verify via `gh auth status`.
**Issue (resolved):** gh authentication token initially lacked `read:project` scope. Remedied by refreshing the token with explicit `read:project` scope (Settings > Developer Settings > Personal access tokens).

**Follow-up (2026-05-08):** WarHub project board (#2) was later queried successfully and confirmed to have **no `Type` field**. For issue parentage, GraphQL `addSubIssue` works reliably; REST `/sub_issues` returns 404.

#### Backlog Inventory (32 Open Issues)

**By Priority:**
| Priority | Count | % | Category |
|----------|-------|---|----------|
| **backlog** | 18 | 56% | Future work, scope undecided |
| **high** | 7 | 22% | Should be done next |
| **medium** | 3 | 9% | Good value, moderate effort |
| (no priority) | 3 | 9% | Recent issues, untagged |
| **Total** | **32** | **100%** | |

**By Area:**
| Area | Count | % | Ownership |
|------|-------|---|-----------|
| **spec-coverage** | 17 | 53% | Engine adapters, new conformance tests |
| **framework** | 6 | 19% | Spec runner, loader, tooling, infra |
| **newrecruit** | 5 | 16% | NR engine-specific features & compliance |
| **devex** | 2 | 6% | Developer experience, refactoring |

#### Key Decisions

1. **Adopt current label scheme as-is.** It's coherent and matches team areas.
2. **Prioritize token scope fix within this sprint.** It unlocks automation.
3. **Triage recent untagged issues (#199, #197, #196) today.**
4. **Unblock design decision for #18 (Data Editor Epic) — resolve within 3 days.**
5. **Set milestones for next 2 major epics** to establish delivery cadence.
6. **Bug triage: hotfix #187 & #186 or backlog?** Recommend hotfix if test coverage impact is high; otherwise backlog with `priority: medium`.

#### Follow-Up: Issue #19 Split Execution

- **#201** (`LoadRosterCommand` + `SaveRosterCommand`) -> sub-issue of **#15** via GraphQL `addSubIssue`
- **#202** (Data Editor conformance MVP) -> sub-issue of **#18** via GraphQL `addSubIssue`
- `enhancement` label applied to both issues
- **Technical note:** `addSubIssue` is the reliable method for setting parent issues on this org; REST `/sub_issues` returns 404

#### Approved by

Avasarala  
Co-authored by: Copilot <223556219+Copilot@users.noreply.github.com>

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

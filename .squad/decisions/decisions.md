# Project Decisions Log

## Decision: Main is protected — always use PRs

**Date:** 2026-05-08  
**Source:** User directive (Amadeusz Sadowski)

### Decision

Main branch is protected. All changes must go through pull requests. Direct commits to main are not permitted.

### Rationale

Branch protection rules enforce code review and CI gates. All agents and contributors must branch first, then open a PR.

---

## Decision: Issue Triage Complete — 2026-05-08

**Author:** Avasarala (Lead)  
**Date:** 2026-05-08  
**Status:** Complete  
**Scope:** Triage of 4 untagged/partially-tagged GitHub issues

### Summary

Processed triage of 4 issues (#199, #197, #187, #186) in WarHub/battlescribe-spec. Applied area and priority labels; identified and resolved 1 duplicate.

### Triage Decisions

#### #199 — "kitchen-sink spec: verify all protocol types are covered"
**Status:** ❌ Closed as duplicate  
**Rationale:** Duplicate of #197 (identical scope, less detailed). Consolidated to single canonical issue.  
**Action:** Closed with duplicate marker pointing to #197.

#### #197 — "kitchen-sink spec should cover all protocol types"
**Status:** ✅ Labeled & triaged  
**Labels:** `area: spec-coverage` + `priority: high` + `enhancement` (existing)  
**Priority Rationale:** Protocol coverage verification is **essential for conformance suite integrity**. Currently blocks PR #195 review. High priority to close the gap.  
**Area Rationale:** Spec coverage expansion — directly owned by spec-coverage area.

#### #187 — "Bug: constraint-shared-linked spec uses duplicate constraint IDs; shared flag undocumented"
**Status:** ✅ Labeled & triaged  
**Labels:** `area: spec-coverage` + `priority: medium` + `bug` (existing)  
**Priority Rationale:** Impacts specific constraint test coverage but **not release-blocking**. Requires design research on BattleScribe constraint model before implementation. Medium priority allows planned work after high-priority framework fixes.  
**Area Rationale:** Spec data quality issue — spec-coverage area owns this.

#### #186 — "Bug: NR and BS error reporting diverges for shared constraints"
**Status:** ✅ Labeled & triaged  
**Labels:** `area: framework` + `priority: high` + `bug` (existing)  
**Priority Rationale:** Adapter-side error reporting divergence is a **correctness issue**. Blocks confidence in engine conformance validation. Must be fixed before shipping specs.  
**Area Rationale:** Engine adapter code & error handling — framework area owns this.

### Key Insights

1. **Duplicate detection:** #199 and #197 both target kitchen-sink protocol coverage. #197 contains the full context and acceptance criteria; #199 was a simpler restatement. Consolidation reduces issue fragmentation and clarifies canonical goal.

2. **Priority split:** #197 (high) vs #187 (medium) reflects that protocol verification blocks spec reliability, while constraint spec bug is a known data quality issue that can be scheduled later.

3. **Area split:** Framework (#186) vs spec-coverage (#187, #197) correctly separates adapter correctness (framework) from spec content expansion (spec-coverage).

### Label Hygiene

All issues now have complete tagging:
- ✅ Area label (1 of: spec-coverage, framework, newrecruit, devex)
- ✅ Priority label (1 of: high, medium, backlog)
- ✅ Type label (bug/enhancement/etc. preserved from original)

No labels were removed; only additions were made to maintain traceability.

### Next Steps

1. **#197 (high):** Miller or Johnson — Design lint test for protocol coverage verification. Start before next milestone.
2. **#186 (high):** Johnson (NR specialist) — Debug adapter error path and compare against BS error output.
3. **#187 (medium):** Bobbie (spec domain) — Research BattleScribe constraint model (`shared` flag semantics); document findings before implementation.

---

**Archived from:** .squad/decisions/inbox/avasarala-triage-complete.md  
**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>

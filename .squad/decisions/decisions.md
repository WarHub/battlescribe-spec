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

---

## Decision: GitHub Sub-Issue Parentage Applied

**Author:** Alex (Tester / QA)  
**Date:** 2026-05-08  
**Status:** Complete  
**Scope:** GitHub issue link management

### Summary

Applied GitHub sub-issue parentage relationships to the WarHub/battlescribe-spec repository. All 12 child issues have been linked to their parent epics via issue body markers.

### Method Applied

**Method 2** (Edit issue body) — proved to be the reliable approach:
- GitHub sub-issues API (`POST repos/{owner}/{repo}/issues/{issue}/sub_issues`) requires `sub_issue_id` as integer parameter, but returned 404 when tested
- Method 1 (API) was attempted but not available or applicable for this repository
- Method 2 (edit issue body) succeeded for all 12 issues

Each child issue body was updated with a prepended "Part of #{PARENT}" marker.

### Parentage Applied

| Parent Epic | Issue | Child Issues |
|--|--|--|
| #15 (Preloaded Roster Lifecycle) | Title: "Epic: Preloaded roster lifecycle conformance" | #20, #21, #22, #23, #25 |
| #16 (Error Validation Coverage) | Title: "Epic: Error Validation" (inferred) | #177, #187 |
| #109 (Fair Use and Automation Policy) | Title: "Epic: Fair Use and Automation Policy" (inferred) | #88, #89 |
| #181 (Protocol Type System) | Title: "Epic: Protocol Type System" (inferred) | #198, #159 |

### Issues Updated

✅ **Epic #15** (5 children):
- ✓ #20 — Feature: Add preloaded roster setup to spec model
- ✓ #21 — Feature: Add protocol support for loading roster state
- ✓ #22 — Task: Add happy-path preloaded roster specs
- ✓ #23 — Task: Add malformed/invalid roster load specs
- ✓ #25 — Task: Add negative mutation error matrix specs

✅ **Epic #16** (2 children):
- ✓ #177 — Research: understand 'shared' flag behavior on conditions and constraints
- ✓ #187 — Bug: constraint-shared-linked spec uses duplicate constraint IDs; shared flag undocumented

✅ **Epic #109** (2 children):
- ✓ #88 — Legal review: NewRecruit automated testing and robots.txt compliance
- ✓ #89 — Set identifying User-Agent header for NewRecruit automated access

✅ **Epic #181** (2 children):
- ✓ #198 — Validate JSON schemas against their metaschema
- ✓ #159 — feat: JSON Schema for engine adapter JSON-line protocol

### Learnings

- GitHub sub-issues REST API (Method 1) not applicable for this repository
- "Part of #{parent}" marker method (Method 2) is reliable and widely supported
- All 12 issues updated successfully with zero failures
- Method 2 provides an explicit, queryable link in issue body that's visible to all users

### Next Steps

- Monitor GitHub for any UI features that might auto-detect the "Part of" marker
- Consider using Project Board v2 fields as a complementary parent field (Method 3) if more structured metadata is needed

---

## Work Order: Unparented Issue Audit & Prioritization

**Author:** Avasarala (Lead)  
**Date:** 2026-05-08  
**Requested by:** Amadeusz Sadowski  

### Executive Summary

**All 26 non-epic issues in WarHub/battlescribe-spec were unparented (pre-parentage snapshot).** This represents 100% of active work with no explicit epic ownership at the time of audit.

**Action items:**
1. **Immediate:** Parent 23 out of 26 issues to appropriate epics (see table below)
2. **Block 3 backlog issues** until their parent epic design decisions are made
3. **Execute prioritized work order** interleaving quick wins with foundational work

### Key Findings

| Metric | Value |
|--------|-------|
| **Total open issues** | 31 |
| **Epic issues** | 5 (#18, #15, #16, #109, #181) |
| **Non-epic issues** | 26 |
| **Issues with explicit parent link** | 0 (pre-parentage snapshot) |
| **Unparented issues** | 26 / 100% (pre-parentage snapshot) |
| **High priority unparented** | 7 |
| **Medium priority unparented** | 5 |
| **Backlog priority unparented** | 14 |

### Phase 1: Unblock Design (Days 1–3)

**#19 is the primary blocker.** It gates 6 downstream issues (#30, #31, #25, #23, #168–#172). Scope decision workshop needed.

**Outcome:** Yes/no decision on roster loading & editor specs → enables parent #18 and child specs.

### Recommended Prioritized Work Order

**Strategy:** Start with high-impact, unblocked issues to build momentum. Resolve design blockers early. Interleave framework work with spec authoring to avoid long dry spells.

**Phase 1 (Days 1–3):** #19 design workshop  
**Phase 2 (Days 4–14):** High-priority specs, framework fixes (assume #19 is YES)  
**Phase 3 (Week 2+):** Backlog infrastructure & legal

Full breakdown in archived inbox decision.

---

## Decision: Scope of Roster Loading & Editor Specs (Issue #19)

**Author:** Holden (WarHub Engine Specialist)  
**Date:** 2026-05-08  
**Status:** In Review  
**Audience:** Amadeusz (decision maker), team stakeholders  
**Relates to:** #19 (scope), #18 (data editor epic), #15 (roster loading), #168-#170 (downstream)

### Problem Statement

**Issue #19** asks: What is in-scope for roster loading and editor conformance specs?

This is a **design blocker** for Epic #18 (Data Editor Conformance) and gates 6+ downstream issues (#168-#172).

### Scope Options

#### Option 1: Narrow — Roster Format Conformance Only
- Load .ros XML files only (no .rosz compression yet)
- 5-10 basic loading specs
- Low complexity, low risk, independent of editor specs
- Doesn't support editor round-trip use case

#### Option 2: Medium — Roster Loading + Editor Round-Trip (RECOMMENDED)
- Roster file loading (all of Option 1)
- PLUS protocol support for save/reload cycle
- Unified conformance model: "Can you edit game data AND have it stick?"
- Medium complexity, unblocks #18 fully
- Achievable in 2-3 sprints

#### Option 3: Broad — Full Editor Persistence + Import/Export
- All of Option 2
- PLUS game data import/export (.gst/.cat file I/O)
- Full editor ecosystem
- High complexity, higher risk to schedule

### DECISION: Option 2 — Medium Scope (Roster Loading + Editor Round-Trip)

**Chosen:** 2026-05-08  
**Rationale:**
1. Unblocks #18 and downstream issues (#168-#172)
2. Engine-agnostic (protocol defines interface; implementation up to adapters)
3. Achievable in 2-3 sprints without overcommitting
4. Forward-compatible (can add import/export later)

### Implementation Path

**Sprint N (Design):**
- Add `LoadRosterCommand`, `RosterLoadResult` to protocol
- Add `SaveRosterCommand`, `RosterSaveResult` to protocol
- Define "valid roster" rules in conformance docs

**Sprint N+1 (Roster Loading Specs):**
- 5-10 basic loading specs (empty, with forces, with costs, invalid file, etc.)

**Sprint N+2 (Round-Trip Specs):**
- Initial integration with GameData editor specs (#168-#170)

### What's NOT in scope (yet)
- `.rosz` compression support (defer to backlog)
- Game data import/export (Option 3 — backlog for future epic)

### Follow-Up Tracking (2026-05-08)

- Scope split implemented as **#201** (roster loading protocol) and **#202** (data editor MVP)
- Parent links confirmed via GraphQL `addSubIssue`: **#201 -> #15**, **#202 -> #18**
- **Technical note:** On this org, GraphQL `addSubIssue` is the reliable method for setting parent issues; REST `/sub_issues` returns 404

---

## Domain Scope Proposal: Issue #19 — Roster Loading & Editor Specs

**Author:** Bobbie (Domain & Spec Specialist)  
**Date:** 2026-05-08  
**For:** Amadeusz Sadowski  
**Related Issues:** #19, #18, #30, #31  

### Executive Summary

Issue #19 gates Epic #18 (Data Editor Conformance). This proposal clarifies **what "data editor" means in the BattleScribe domain**, what specs are needed, and proposes a **minimum viable scope** that balances coverage with manageable complexity.

**Key Findings:**
- **Roster editing** (loading .ros files, selecting units, modifying costs) is mature: 312 existing specs
- **Data editor specs** (editing .gst/.cat files) are nascent: 10 basic gamedata specs exist
- **Two separate problem spaces** requiring different test infrastructure and spec patterns
- **Minimum scope for #18 MVP:** Basic CRUD for structural entries + validation specs + one round-trip scenario

### Two Distinct Domains

| Aspect | Roster Loading | Data Editor |
|--------|---|---|
| **Files** | `.ros` (roster XML) | `.gst` (game system), `.cat` (catalogue) |
| **Actors** | Player/roster builder | Designer/content creator |
| **Operations** | Load roster → Select units → Manage costs | Create entries → Link data → Validate structure |
| **State** | Roster instance (forces, selections, customizations) | Game data structure (entry definitions, constraints, rules) |
| **Current Coverage** | 312 specs (comprehensive) | 10 specs (proof-of-concept) |

### Minimum Viable Scope for #18 (MVP)

To **unblock Epic #18** and enable downstream work (#30, #31, #167–#173), recommend **Phase 1 MVP:**

**Spec Infrastructure:**
- [ ] Define `addEntry`, `deleteEntry`, `setField`, `setLink` actions in spec models
- [ ] Update GameData spec runner to handle new actions
- [ ] Update engine interface: `IGameDataEngine` with mutation methods

**Structural Entry Specs** (matching #167):
- [ ] Create SelectionEntry (in catalogue, game system, shared)
- [ ] Create ForceEntry (in catalogue, game system)
- [ ] Create CategoryEntry (in catalogue, game system, shared)
- [ ] Set field: name, hidden, type, publicationId
- [ ] Delete leaf entry
- [ ] Nesting: entry within entry (2-3 levels)

**Link Specs** (partial #168):
- [ ] Create entry link (basic case)
- [ ] Create category link (basic case)
- [ ] Validate link targets exist
- [ ] Break link → detect error

**Validation Specs** (partial #31):
- [ ] Duplicate ID detection
- [ ] Missing required field detection
- [ ] Broken link detection
- [ ] Invalid containment detection

**Round-Trip Spec** (partial #30):
- [ ] Load system → modify entry name → save → reload → verify

**Estimated effort:** 4-6 weeks for one developer

### Test Scenarios to Write First (Priority Order)

#### Priority 1: Foundation (Week 1-2)
1. Create SelectionEntry in catalogue
2. Set entry name
3. Create SelectionEntryGroup
4. Delete leaf entry
5. Duplicate ID error

#### Priority 2: Linking (Week 3-4)
6. Create entry link
7. Broken link error
8. Create category link
9. Shared entry visibility

#### Priority 3: Round-Trip & Error Handling (Week 5-6)
10. Mutation round-trip
11. Malformed XML handling
12. Missing required field error
13. Invalid containment error

---

**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>

---

## Decision: Unparented Issue Triage (14 non-epic issues)

**Author:** Avasarala  
**Date:** 2026-05-08  
**Status:** Ready for implementation  
**Scope:** Backlog triage, epic assignment

### Executive Summary

Found **14 unparented non-epic issues** in the backlog. Triaged all 14 and recommend parent epics for each based on issue domain.

### Triage Distribution

**By Epic:**
- **#15 (Preloaded Roster Lifecycle):** 1 issue (#41)
- **#16 (Error Validation Coverage):** 1 issue (#186)
- **#17 (DevEx):** 2 issues (#196, #191)
- **#18 (Data Editor Conformance):** 9 issues (#168-174, #30, #31)
- **#109 (Fair Use / Divergence):** 1 issue (#197)

### Decision

Parent epic assignments for 14 unparented non-epic issues:
- #197 → #109 (Kitchen sink protocol coverage)
- #196 → #17 (Linter improvements)
- #191 → #17 (NR debugging skill)
- #186 → #16 (Error reporting divergence)
- #174, #173, #172, #171, #170, #169, #168 → #18 (Data editor features)
- #41 → #15 (Roster file I/O)
- #31, #30 → #18 (Editor specs)

### Rationale

- **#15:** Roster lifecycle includes file I/O and round-trip operations
- **#16:** Error validation coverage for concrete error divergence bugs
- **#17:** Developer experience improvements (tooling, debugging, automation)
- **#18:** Primary data editor conformance epic; all editor subsystem features
- **#109:** Kitchen sink vehicle for systematic divergence analysis

### Status

High confidence. Mappings consistent with existing epic scopes. Fully reversible if scope changes.

---

**Archived from:** .squad/decisions/inbox/avasarala-unparented-triage.md  
**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>

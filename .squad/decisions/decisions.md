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

> ⚠️ **SUPERSEDED 2026-08-13 — the "Learnings" below are wrong.** The sub-issues API works fine on
> this repository; the 404 was a caller error, not a capability limit. Writing `Part of #N` in an
> issue body creates **no** parent link, so issues parented this way appear unparented on the board.
> See "Decision: Sub-issue parentage is a real link, not body prose" at the end of this file.

**Author:** Alex (Tester / QA)  
**Date:** 2026-05-08  
**Status:** Complete (method since corrected)  
**Scope:** GitHub issue link management

### Summary

Applied GitHub sub-issue parentage relationships to the WarHub/battlescribe-spec repository. All 12 child issues have been linked to their parent epics via issue body markers.

### Method Applied

**Method 2** (Edit issue body) — proved to be the reliable approach:
- GitHub sub-issues API (`POST repos/{owner}/{repo}/issues/{issue}/sub_issues`) requires `sub_issue_id` as integer parameter, but returned 404 when tested
- Method 1 (API) was attempted but not available or applicable for this repository
- Method 2 (edit issue body) succeeded for all 12 issues

Each child issue body was updated with a prepended "Part of #{PARENT}" marker.

> ⚠️ The 404 was caused by passing the issue **number** as `sub_issue_id`. That parameter takes the
> issue's **database id** (e.g. `5138734670`), not its number (`421`). Method 1 works.

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

> ⚠️ Every bullet in this section is wrong. Corrected 2026-08-13 — see the superseding entry.

- ~~GitHub sub-issues REST API (Method 1) not applicable for this repository~~ — it is; the 404 was
  a wrong `sub_issue_id` (issue number instead of database id).
- ~~"Part of #{parent}" marker method (Method 2) is reliable and widely supported~~ — it is prose.
  GitHub does not read it. It creates no link, and the board's Parent field stays empty.
- ~~Method 2 provides an explicit, queryable link in issue body that's visible to all users~~ — it is
  not queryable: `parent`/`subIssues` in the API and the board's Parent field both stay null.

### Next Steps

- ~~Monitor GitHub for any UI features that might auto-detect the "Part of" marker~~ — there are
  none and there will be none; use the sub-issue API.

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

---

## Decision: KitchenSinkCoversAllProtocolTypes lint test design

**Author:** Alex (Tester / QA)  
**Date:** 2026-05-08  
**Status:** Complete  
**Scope:** Lint test design, `tests/Infrastructure/SpecLintTests.cs`  
**Closes:** #197

### Summary

Added `KitchenSinkCoversAllProtocolTypes` fact to `SpecLintTests` that enforces `protocol-kitchen-sink.yaml` exercises every Protocol type, every known action, and every expected-state field.

### Design Decisions

#### 1. Excluded types from Protocol type scan

| Type | Reason |
|------|--------|
| `ProtocolError` | Error wrapper — not a spec setup data type |
| `ProtocolDataFile` | Intermediate deserialization wrapper — not emitted by spec authors |
| `ProtocolJsonContext` | Source-generated `System.Text.Json` context class — not a data type |

Abstract types (e.g. `ProtocolSerializer`) are excluded automatically via `!t.IsAbstract`.

#### 2. `dump` excluded from action coverage

`dump` is a debugging action, not a spec conformance action. It has no effect on roster state and would pollute the kitchen-sink spec if required. Excluded via `.Except(["dump"])`.

#### 3. `SelectionCount` OR logic

`SelectionCount` is a field on both `ExpectedStateDef` (roster level) and `ExpectedForceDef` (force level). The kitchen-sink spec checks `selectionCount` at force level, not roster level. The lint test checks that it appears at roster OR force level to avoid a false failure without weakening the requirement.

#### 4. Required field detection

Required fields are determined by **hard-coded lists** in `SpecLintTests.cs`, not by reflection-based nullability. Each list enumerates property names that `protocol-kitchen-sink` must exercise at least once:

- `RequiredExpectedStateFields`: `Name`, `ForceCount`, `Forces`, `CostCount`, `Costs`, `CostLimits`, `CostLimitCount`, `GameSystemName`, `GameSystemId`, `ErrorCount` (plus OR checks for `Errors`/`ErrorsContain` and `SelectionCount`)
- `RequiredExpectedForceFields`: `Name`, `EntryId`, `CatalogueName`, `CatalogueId`, `CategoryCount`, `Categories`, `Publications`, `SelectionCount`, `Selections`, `Rules`, `Profiles`, `CustomName`, `CustomNotes`, `ChildForceCount`, `ChildForces`, `AvailableEntryCount`, `PublicationId`, `Page`, `Hidden`
- `RequiredExpectedSelectionFields`: `Name`, `Type`, `Number`, `EntryId`, `EntryGroupId`, `Costs`, `Profiles`, `Rules`, `Categories`, `Children`, `ChildCount`, `Page`, `PublicationId`, `PublicationName`, `CustomName`, `CustomNotes`, `Hidden`

Note: these lists intentionally include many **nullable** fields (e.g., `Page`, `Hidden`, `PublicationId`, `CustomName`, `CustomNotes`) because the kitchen-sink spec is required to demonstrate those fields are observable, even though omitting them in other specs is valid.

### Outcome

Test runs with 392 passing + 1 expected failure, reporting exactly:
- `[action] 'duplicateForce' not exercised`
- `[ExpectedStateDef] field 'Name' never set`

These are Bobbie's pending additions. Test will go green once those are added to the spec.

---

**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>

---

## Decision: Protocol Kitchen-Sink Coverage Complete (Issue #197)

**Author:** Bobbie (Domain & Spec Specialist)
**Date:** 2026-05-09
**Status:** Complete
**Scope:** `specs/roster/protocol/protocol-kitchen-sink.yaml` + `docs/spec-schema.json`
**Closes:** #197

### Summary

Extended `protocol-kitchen-sink.yaml` to cover all protocol types, all action types, and all expected-state assertion fields required by the `KitchenSinkCoversAllProtocolTypes` lint test.

### Changes Made

**`specs/roster/protocol/protocol-kitchen-sink.yaml`:**
- Added `ProtocolModifierGroup` + `ProtocolConditionGroup` to `se-commander`'s `modifierGroups`
- Added `ProtocolRepeat` to `se-commander`'s second modifier (never fires: `atLeast 99 infantry`)
- Added `fe-platoon` nested inside `fe-battalion` (for `addChildForce`)
- Added `fe-hidden-detachment` (hidden:true) as gameSystem forceEntry (for `ExpectedForceDef.Hidden`)
- Added `se-inf-hidden-upgrade` (hidden:true) to se-infantry (for `ExpectedSelectionDef.Hidden`)
- Added 6 new steps: step 8b/8c (hidden selection select/deselect), step 9a (errorsContain subset), step 9b/9c (hidden force add/remove), step 9d/9e (addChildForce/remove), step 9f/9g (duplicateForce with `skipEngines: [battlescribe]`)
- Updated final expectedState: added `errorCount`, `gameSystemId`, `costCount`, `childCount`, `childForces`

**`docs/spec-schema.json`:**
- Added `skipEngines` field to the `step` definition (array of strings). Schema was previously missing this field introduced alongside `SkipEngines` in `StepDef`.

### Key Design Decisions

1. **`duplicateForce` with `skipEngines: [battlescribe]`** — BS engine throws `NotSupportedException`. The action is in the spec for lint coverage but skipped for BS via `skipEngines`. The expectedState after it uses `engines: battlescribe:` override to assert the correct force count (1, not 2).

2. **`errorsContain` and `errorCount` in separate steps** — Validator enforces mutual exclusivity. Step 9a exercises `errorsContain`, final step exercises `errorCount`.

3. **Hidden upgrade error is in BOTH engines** — BS generates the same hidden-entry error as NR. The error is on the parent selection (`se-infantry/hidden`), not the hidden child itself.

4. **gameSystem force entries don't auto-select** — Forces added with `forceEntryId` pointing to a gameSystem entry (not catalogue) don't auto-select minimum-constraint selections from the linked catalogue. `selectionCount: 0` is correct for `fe-hidden-detachment`.

5. **BS returns forces in oldest-first order** — `[Battalion, Hidden Detachment]` for BS (not newest-first as assumed earlier).

---

**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>


---

# Decision: KitchenSinkProtocolTypeExclusions — Only Protocol* Types Belong

**Date:** 2026-05-12  
**Author:** Alex (QA)  
**Related:** `tests/Infrastructure/SpecLintTests.cs`, `KitchenSinkCoversAllProtocolTypes`

## Decision

`KitchenSinkProtocolTypeExclusions` must only contain types whose names start with `Protocol`.  
The scan filter (`t.Name.StartsWith("Protocol")`) makes all non-`Protocol*` entries unreachable dead code.

## Rationale

Adding command/response types (SetupCommand, ActionResult, etc.) to the exclusion set provides false documentation — it implies they are candidates for the coverage check when they are not. This confused reviewers and could mislead future maintainers into thinking the scan would catch them.

## Rule

> When adding a new entry to `KitchenSinkProtocolTypeExclusions`, verify it starts with "Protocol". If it doesn't, it doesn't belong in this set.

---

# Decision: Check for Git Conflict Markers in Spec Lint

**Date:** 2026-05-12  
**Author:** Alex (QA)  
**Related:** `specs/roster/protocol/protocol-kitchen-sink.yaml`, `tests/Infrastructure/SpecLintTests.cs`

## Problem

A stray `>>>>>>> ...` git conflict marker was committed in `protocol-kitchen-sink.yaml`. This caused a cryptic YAML parse error that manifested as `Assert.NotNull() Failure` in `KitchenSinkCoversAllProtocolTypes`, with no obvious connection between the test failure and the root cause.

## Decision

Consider adding a lint rule in `SpecLintTests.AllLintChecks` that detects git conflict marker patterns (`<<<<<<<`, `=======`, `>>>>>>>`) in spec YAML files and fails with a clear error message. This would make such failures immediately actionable instead of silently failing spec load.

## Current Workaround

Use `dotnet run --project src/BattleScribeSpec.Debugger -- {spec-id}` to directly load a failing spec — it prints YAML parse errors clearly.


---

# Decision: SkipEngines XML doc fix + decisions.md accuracy fix

**Author:** Avasarala (Lead)
**Date:** 2026-05-12
**Status:** Complete
**Scope:** `src/BattleScribeSpec.TestKit/Roster/RosterSpecModels.cs`, `.squad/decisions/decisions.md`

---

## Issue 1: SkipEngines XML doc was misleading (RosterSpecModels.cs:111)

### Finding

The XML doc for `StepDef.SkipEngines` stated:

> "When skipped, empty outputs are stored so downstream expressions resolve to null."

This is incorrect. When a step is skipped, `ExpressionResolver.StoreOutputs(id, new ActionOutputs())` is called, which stores an `ActionOutputs` with all fields null. The effect is:

- **Prevents:** "step not found" `InvalidOperationException` for the skipped step's ID.
- **Does NOT prevent:** `InvalidOperationException` for any expression like `${{ steps.id.forceId }}` — `ExpressionResolver.ResolveField` throws explicitly when `ForceId` is null.

Expressions do **not** resolve to null; they throw. The documentation created a false expectation.

### Decision

Fix the XML doc — not the code. Making `ExpressionResolver` return `null` for skipped-step fields would propagate nulls silently to callers that then throw with less informative errors. The current throw-at-the-resolver behavior is correct; only the doc was wrong.

### Change

Updated the XML doc to say: empty outputs prevent "step not found" errors but expressions referencing specific output fields still throw; spec authors must not reference skipped-step outputs from downstream steps targeting those same engines.

---

## Issue 2: decisions.md "Required field detection" section was inaccurate (line 435)

### Finding

The decision doc claimed required-field detection used reflection-based nullability (non-nullable = required, nullable = optional). The actual implementation uses **hard-coded lists** (`RequiredExpectedStateFields`, `RequiredExpectedForceFields`, `RequiredExpectedSelectionFields`) in `SpecLintTests.cs`. These lists include many nullable fields — `Page` (`string?`), `Hidden` (`bool?`), `PublicationId` (`string?`), `CustomName` (`string?`), `CustomNotes` (`string?`) — because the kitchen-sink spec must demonstrate that all observable fields are reachable, regardless of nullability.

### Decision

Update the decision doc to describe the actual implementation: named hard-coded lists, the specific nullable fields included in each, and the rationale (kitchen-sink must demonstrate observability of all fields, not just non-nullable ones).

---

## Decision: Sub-issue parentage is a real link, not body prose

**Date:** 2026-08-13  
**Source:** Board grooming review of [Conformance Spec (project 2)](https://github.com/orgs/WarHub/projects/2)  
**Supersedes:** "Decision: GitHub Sub-Issue Parentage Applied" (2026-05-08)

### What was wrong

That entry recorded a "learning" that the GitHub sub-issues API is *"not applicable for this
repository"*, and established writing `Part of #{parent}` in the issue body as the house method.

Both halves are false, and the second one is the expensive one. `Part of #N` is prose. GitHub does
not parse it. An issue parented that way has `parent: null` in the API and an empty **Parent** field
on the board — so it is, by every mechanical measure, unparented. The convention silently produced
the exact defect a later audit went looking for.

The 404 that caused it was a caller error: `sub_issue_id` takes the issue's **database id**, not its
number. Verified working on this repository on 2026-08-13.

### The rule

**Parentage is set through the API, and the body-text equivalents are deleted** — the `Part of #N`
line on the child and any `## Children` checklist on the parent. Keeping both means keeping two
records that drift, and only one of them drives the hierarchy.

```bash
# read a parent's children
gh api repos/WarHub/battlescribe-spec/issues/419/sub_issues --jq '.[] | "\(.number) \(.title)"'
```

```bash
# link child 421 under parent 419 — note: database id, not issue number
gh api --method POST repos/WarHub/battlescribe-spec/issues/419/sub_issues -F sub_issue_id=5138734670
```

Get a child's database id with
`gh api repos/WarHub/battlescribe-spec/issues/421 --jq .id`. The GraphQL `addSubIssue` mutation is
equivalent and takes node ids (`issueId`, `subIssueId`). Children keep insertion order, so add them
in the order they should read, and link a new child at creation time rather than writing prose that
someone has to migrate later.

### Why it matters beyond tidiness

An epic's sub-issue list is what makes "is this epic done?" answerable without reading it. #15, #16,
#18 and #73 all report progress from that list. A child linked only in prose is invisible to it, so
the epic reports itself more complete than it is.

---

**Co-authored by:** Copilot <223556219+Copilot@users.noreply.github.com>


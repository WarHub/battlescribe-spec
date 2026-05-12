# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, YAML specs (specs/roster/, specs/gamedata/), BattleScribe data model
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- Spec format: setup (gameSystem/catalogue/force/selection), actions, expectedState
- After editing specs: run `pwsh -File tools\format-specs.ps1` then `dotnet test --filter "DisplayName~SpecLint"`
- Debugger: `dotnet run --project src/BattleScribeSpec.Debugger -- {spec-id}` for step-by-step inspection
- Setup data must be minimal but complete; expectedState assertions specific but not brittle
- 312 roster specs across 17 categories; 10 gamedata specs in 1 category (as of creation)
- Collective, import, publication flags are key domain concepts in the BS data model

## Research: Issue #19 Domain Scope (2026-05-08)

**Key Finding:** Roster loading vs data editor are TWO SEPARATE PROBLEM SPACES.
- **Roster loading:** Engine interprets pre-authored .ros files; 312 existing specs mature; focus on negative specs (malformed input).
- **Data editor:** Engine mutates .gst/.cat files; 10 nascent gamedata specs; needs complete action infrastructure (addEntry, deleteEntry, setField, setLink).

**Current State:**
- Roster spec infrastructure: Complete (12 action types: addForce, selectEntry, setCostLimit, setCustomization, duplicate, etc.)
- GameData spec infrastructure: Minimal (1 action: addEntry); no delete, field mutation, validation, round-trip actions.

**MVP Recommendation for Epic #18 (Data Editor):**
- Minimum viable = Phase 1 (13 specs): basic CRUD (create/delete) + field mutations (name/hidden/type) + validation (duplicate ID, broken link, invalid containment) + ONE round-trip scenario.
- NEW actions needed: addEntry, deleteEntry, setField, setLink, validateData.
- Estimated effort: 6 weeks (1 dev).
- Defer to Phase 2: Copy/paste, import, find/replace; Advanced logic (modifiers, repeats); Automation/bulk operations.

**Scope Proposal:** Written to `.squad/decisions/inbox/bobbie-19-scope-domain.md` with:
1. Domain clarification (roster vs editor with table comparison)
2. What "valid" means in each domain
3. Sample scenarios for both (15 concrete examples for data editor)
4. Current coverage gaps (roster 95% done, editor 2% done)
5. Minimum viable scope (4-6 week MVP)
6. Priority test scenario list (13 specs ranked by dependency)
7. Out-of-scope features (with rationale + timeline)
8. Recommendations for Amadeusz (split #19 tasks, proceed with #18 MVP, defer phases 2-3)

### Team Decision: Option 2 Approved (2026-05-08)

**DECISION:** Amadeusz approved Option 2 (Medium scope) for Issue #19.
- Holden's technical analysis evaluated 3 scope options; Bobbie's domain analysis clarified the problem space.
- **Option 2 chosen:** Roster loading + editor round-trip.
- **MVP for Epic #18:** Phase 1 scope (13 priority specs) now documented. Bobbie to implement specs in priority order.
- **Phase 1 scenarios (first 5 specs):** Create SelectionEntry, Set entry name, Create SelectionEntryGroup, Delete leaf entry, Duplicate ID error detection.

## Research: Shared Flag Semantics (2026-05-12)

**Finding:** NR ignores `shared=false` on conditions and repeats at force level (fires as `shared=true`), but correctly handles constraints. For entries nested inside a parent selection, NR also doesn't fire shared=false conditions — but for a different reason than BS.

- **CatXmlGenerator correctly emits `shared` attribute** for conditions, repeats, AND constraints. No adapter bug.
- **NR root cause (JS source analysis, bundle `BA2pibXD.js`):** `hash()` excludes `shared` from the listener key. Constraints work because NR explicitly substitutes `childId` with the per-link composite ID when `shared===false`. Conditions/repeats have no such substitution — they always hash to the same bucket as `shared=true`.
- **NR `find("force", shared=false)`** returns `this.parent` instead of traversing to force. For direct force children `this.parent` IS the force (no difference). For nested entries `this.parent` is the container, whose reactive counters have no base-ID entry → count=0 → condition doesn't fire (correct result, wrong reason).
- **BS behavior:** `shared=false` → matches by composite entry-link ID → base `childId` never matches → condition/repeat never fires regardless of nesting.
- **Resolution:** Per-engine `engines: newrecruit:` overrides on `expectedState` to document NR divergence. No `newrecruit: skip` flags.
- **Adapter fix:** `BattleScribeEngine.cs` `errorIdMap` changed from Dictionary to multimap — previously overwrote entries for the same shared entry, misattributing constraint errors to the wrong constraint ID.

**Deliverables:**
- `docs/shared-flag-semantics.md`: complete reference with JS root cause, engine × element × nesting tables
- `condition-shared-flag`: merged spec (shared=true vs shared=false, NR override for force-level divergence)
- `condition-shared-flag-nested`: NEW — nested case; both engines agree (different mechanisms)
- `modifier-repeat-shared-flag`: merged spec (same approach as condition)
- `constraint-shared-flag`: renamed from `constraint-not-shared-per-link`; contrasts both shared values with error-proving
- 1428 specs pass on BS and NR frozen

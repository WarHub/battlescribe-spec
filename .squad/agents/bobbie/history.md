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

## Research: Shared Flag NR Behavior (2026-05-12)

**Finding:** NR ignores `shared=false` on conditions and repeats — it always uses shared counting (equivalent to `shared=true`).

- **CatXmlGenerator correctly emits `shared` attribute** for conditions, repeats, AND constraints. No adapter bug in XML serialization.
- **NR engine behavior:** With `shared=false`, BS matches by composite entry-link ID (e.g., `link-alpha::shared-trigger`), so conditions/repeats referencing the base `childId` never match → condition never fires / repeat stays at 0. NR ignores this distinction entirely and always resolves to the base shared ID.
- **Resolution:** Used per-engine `engines: newrecruit:` overrides on `expectedState` assertions to test NR's actual behavior alongside BS's correct behavior. No whole-spec `newrecruit: skip` needed.

**Spec changes:**
- Merged `condition-shared-counting` + `condition-not-shared-per-link` → `condition-shared-flag` (tests both shared=true and shared=false in one scenario, with NR overrides)
- Merged `modifier-repeat-shared-counting` + `modifier-repeat-not-shared-per-link` → `modifier-repeat-shared-flag` (same approach)
- Renamed `constraint-not-shared-per-link` → `constraint-shared-flag` (kept separate from `constraint-entry-link-shared-counting` which tests scope=roster vs scope=force — genuinely different scenario)
- All specs pass on BS and NR frozen

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
- Formatter logic lives in `src/BattleScribeSpec.TestKit/SpecFormatter.cs` (Pass 1: `StripTrailingWhitespaceAndRedundantLines`). To add a new "remove redundant field" rule: add the stripped pattern to the `is` match in Pass 1 AND add a corresponding `CheckNo*` method in `SpecLintTests.cs`, wired into `AllLintChecks`.
- Worktrees need `git submodule update --init` — the `.deps/wham` submodule is not automatically initialized in new worktrees.
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

## Research: Issue #177 — `shared` Flag Semantics (2026-05-08)

**Key Finding:** The `shared` flag on constraints/conditions/repeats controls HOW the engine identifies selections when counting — by the shared entry's original ID (cross-link) vs the composite link ID (per-link).

**Behavior summary:**
- `shared=true` on constraints: counts selections across ALL entry links referencing the same shared entry (unified limit)
- `shared=false` on constraints: each entry link counts independently (per-link limit)
- `shared=true` on conditions: same semantic — counts across all links for condition evaluation
- `shared=true` on repeats: analogous (documented, not yet spec'd)

**Existing specs reviewed (all correct):**
- `constraint-shared`, `constraint-shared-deduplication`, `constraint-shared-linked`
- `constraint-entry-link-shared-counting` (the strongest proof of shared counting behavior)
- `constraint-entry-link-shared-target`

**New specs created:**
- `condition-shared-counting`: proves shared=true on conditions counts across entry links
- `constraint-not-shared-per-link`: proves shared=false means independent per-link counting
- `condition-not-shared-per-link`: proves shared=false on conditions never fires (childId mismatch)
- `modifier-repeat-not-shared-per-link`: proves shared=false on repeats keeps multiplier at zero

### PR #208 Extended Work (2026-05-12)

**Adapter bug fixed:** `BattleScribeEngine.CollectElementErrors` used `errorIdMap[entryId]` as single-value dict, overwriting when multiple constraints exist on same shared entry. Changed to multimap + value-matching via `ResolveConstraintFromEntry`.

**shared=false deeper discovery:**
- Constraints: per-link counting (independent limits) — works as expected
- Conditions/Repeats: effectively DISABLED when childId references a shared entry via links. Engine matches by composite entry-link ID (e.g., `link-alpha::shared-trigger`) which never matches raw childId (`shared-trigger`). This is NOT per-link counting — it's no counting at all.
- NR ignores shared=false on conditions/repeats entirely — always behaves as shared=true. Both new specs skip NR.

**NR compatibility fix:** Removed `messageContains` from `constraint-not-shared-per-link` — NR uses "max N" vs BS "maximum N". With adapter fix, constraintId alone is sufficient to distinguish errors.

**Key paths:**
- `ProtocolMessages.cs:553` — `Shared` on ProtocolConstraint
- `ProtocolMessages.cs:602` — `Shared` on ProtocolCondition
- `ProtocolMessages.cs:634` — `Shared` on ProtocolRepeat
- `JavaModelFactory.cs:997` — `c.setShared(shared)` on constraint creation
- `JavaModelFactory.cs:1058` — `c.setShared(shared)` on condition creation

**PR:** #208
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
## Research: Issue #197 Protocol Kitchen-Sink Coverage (2026-05-09)

**Task:** Extend `protocol-kitchen-sink.yaml` to cover all missing Protocol types and expected-state fields per lint requirement in `KitchenSinkCoversAllProtocolTypes`.

**Key Findings:**

- `duplicateForce` is NOT supported in BS engine (`NotSupportedException` thrown). It must be in kitchen-sink for lint compliance but must use `skipEngines: [battlescribe]` on the action step.
- The JSON schema (`docs/spec-schema.json`) did NOT include `skipEngines` — schema must be updated when a new action-level field is added to `StepDef`.
- `errorsContain` and `errorCount` are **mutually exclusive** in a single `expectedState` (validated in `SpecValidator.cs`). Use different steps to exercise each.
- `errorsContain` and `errors` are also mutually exclusive.
- When a hidden selection entry is selected in BS: the error owner is the **PARENT** selection (e.g. `se-infantry`), not the hidden child entry itself. Error format: `selection {parentEntryId} <- {parentEntryId}/hidden`.
- BS returns forces in **add-chronological order** (oldest first). When adding Hidden Detachment after Battalion, order is `[Battalion, Hidden Detachment]`.
- Hidden force entries added from the **gameSystem** (not catalogue) do NOT auto-select minimum-constraint entries from the catalogue. `fe-hidden-detachment` (in gameSystem) + `catalogueId: cat-1` → `selectionCount: 0`, not 1.
- `format-specs.ps1` can corrupt YAML if a comment (using em dashes `—`) is on the same line as a scalar value. Always put step-header comments on their own line with a blank line separator.
- `errorsContain` subset matching uses `on:` = error owner and `from:` = `{entryId}/{constraintId}`.
- Engine-specific assertions in `expectedState` use `engines: battlescribe: {overrides}` (zone 3, last).
- New protocol setup types exercised: `ProtocolModifierGroup`, `ProtocolConditionGroup`, `ProtocolRepeat`.
- New actions exercised: `addChildForce`, `removeForce` (for child force).
- New expected-state fields exercised: `errorCount`, `gameSystemId`, `costCount`, `hidden` (force), `childForces`, `childForceCount`, `childCount` (selection).

**Outcome:** All lint checks pass, schema valid, BS engine conformance passes. NR frozen tests are pre-existing failures (Playwright browser not installed in environment). Closes #197.
## PR #208 Review Redesign (2026-05-10)

- `shared=true` on separate non-shared entries with different constraint IDs is a no-op — no cross-entry counting occurs. Only same-ID + shared=true enables cross-entry behavior.
- BS engine attributes ALL constraint errors on a shared entry to the `shared=true` constraint's ID, even when multiple constraints with different shared flags exist. Use `messageContains` to distinguish them.
- `shared=true` on repeats is confirmed to work analogously to conditions — counts selections across all entry links referencing the same shared entry.
- Specs that only prove "no error" are weak — always add an error-proving step that exceeds the limit.
- The `duplicate-ids` tag in specs opts out of the SetupIdValidator duplicate ID check.
- **Adapter constraintId bug (fixed):** BattleScribeEngine's `errorIdMap` used `Dictionary<string, (...)>` keyed by entryId, overwriting when multiple constraints existed on the same shared entry. Fixed by changing to `Dictionary<string, List<string>>` (multimap) and adding `ResolveConstraintFromEntry` for value-matching resolution. The "all errors attributed to shared=true constraint ID" was an adapter bug, NOT actual BS engine behavior.
- `shared=false` on conditions/repeats with `childId` referencing a shared entry effectively disables cross-link matching — the engine matches by composite entry-link ID, which never matches the raw `childId`. This is consistent behavior, not a bug.
- When editing adapter code in a git worktree, changes must be made to the WORKTREE path (e.g., `D:\repos\battlescribe-spec-177\src\...`), not the main repo path.

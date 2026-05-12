# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, NUnit, test profiles (lint, bs, nr-frozen, nr-editor-frozen, pre-push)
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- Always run `dotnet test -p:TestProfile=pre-push` before declaring anything ready
- Filter to single spec: `dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~{spec-id}"`
- Spec lint in SpecLintTests.cs and GameDataSpecLintTests.cs
- Frozen NR tests replay a single HAR snapshot — all specs share the same HAR; no per-spec HAR needed
- Game-system-only specs (no catalogues) must skip nr-editor profile
- Debugger: `dotnet run --project src/BattleScribeSpec.Debugger -- {spec-id}` for full roster dump
- GitHub sub-issues API (`POST repos/{owner}/{repo}/issues/{issue}/sub_issues`) requires `sub_issue_id` as integer, but returns 404. Method 2 (edit issue body to prepend "Part of #{parent}") works reliably.

## Work Completed: GitHub Sub-Issue Parentage (2026-05-08)

**Task:** Applied parentage relationships to 12 child issues across 4 parent epics.
- Method 2 (issue body edits with "Part of #{parent}" marker) used for all 12 issues.
- **Epic #15** (5 children): #20, #21, #22, #23, #25
- **Epic #16** (2 children): #177, #187
- **Epic #109** (2 children): #88, #89
- **Epic #181** (2 children): #198, #159

**Status:** ✅ Complete. All 12 issues updated successfully. Zero failures.

## Team Decision Impact (2026-05-08)

**Cross-epic effect:** Alex's parentage work enables prioritized execution of high-priority issues:
- High-priority independent issues (#197 kitchen-sink, #186 error divergence) tracked separately.
- Parented issues now queryable for phase-based execution (Phase 1: #19 blocker; Phase 2: high-priority specs; Phase 3: backlog).

### Set parent issue + Type on #201 and #202 via GraphQL (2026-05-08)
- **Parent links set:** #201 -> #15, #202 -> #18 via `addSubIssue` GraphQL mutation
- **Labels applied:** `enhancement` on both issues
- **Key learning:** `addSubIssue` mutation works on this repo; REST `/sub_issues` endpoint returns 404
- **Project board finding:** WarHub project board (#2) has no `Type` field, so no Type value could be set

## Work Completed: Extended Sub-Issue Linking (2026-05-08)

**Task:** Applied 6 additional sub-issue parent links to issues from Avasarala's unparented audit.
- **Method:** Issue body edits with "Part of #{parent}" marker (REST API returns 404)
- **Links applied:**
  - #186 → #16 (Error Validation)
  - #187 → #16 (Error Validation)
  - #191 → #17 (Structured validation errors)
  - #196 → #17 (Structured validation errors)
  - #197 → #109 (Fair Use / Divergence)
  - #198 → #181 (Protocol Type System)

**Epic names discovered:**
- #16: NR/BS error handling divergence
- #17: Structured validation error model
- #109: NewRecruit automated testing & robots.txt compliance
- #181: JSON Schema/numeric precision

**Status:** ✅ Complete. All 6 links verified on GitHub.

## [2026-05-08 04:58:43] Body-Fix Mission — 11 Issues Repaired

### Root Cause
gh api body-prepend via string concatenation strips all internal newlines when using -f body="..." syntax.

### Failure Mode
When editing issue bodies with:
```powershell
gh api repos/{owner}/{repo}/issues/{number} -f body="Part of #N`n$body"
```
Result: All newlines in `$body` are stripped, corrupting markdown.

### Fix Pattern (ALWAYS USE)
```powershell
$body = gh issue view {NUMBER} --json body -q .body
# modify $body preserving newlines
$tmp = [System.IO.Path]::GetTempFileName()
$body | Set-Content -Path $tmp -Encoding UTF8 -NoNewline
gh issue edit {NUMBER} --body-file $tmp
Remove-Item $tmp
```

### Issues Repaired (All 11)
#20, #21, #22, #23, #25, #177, #187, #88, #89, #198, #159

### Status
✓ Complete. All 11 bodies restored. Skill extracted.

## Work Completed: Fix Copilot Review Comments in SpecLintTests.cs (2026-05-12)

**Task:** Address two Copilot review comments on `tests/Infrastructure/SpecLintTests.cs`.

### Fix 1 — CollectAllSelections recursion (line 951)

**Problem:** `CollectAllSelections` only traversed top-level forces + one level of ChildForces. Grandchild forces and deeper were not collected.
**Fix:** Extracted `CollectForceRecursive` local function that calls itself for each child force. Now ChildForces are traversed to any depth.

### Fix 2 — KitchenSinkProtocolTypeExclusions dead entries (line 691)

**Problem:** The exclusion set contained 11 non-`Protocol*` types (SetupCommand, ActionCommand, SetupResult, etc.). Because the filter uses `t.Name.StartsWith("Protocol")`, these can never appear in `expectedTypes` and their exclusion entries are unreachable dead code.
**Fix:** Removed all 11 non-Protocol* entries. Kept only `ProtocolError`, `ProtocolDataFile`, and `ProtocolJsonContext` — the three Protocol-prefixed types that are intentionally excluded.

### Bonus fix — stray git conflict marker in protocol-kitchen-sink.yaml

**Discovery:** The spec had a stray `>>>>>>> 368b288 (...)` conflict marker at line 11 causing a YAML parse error. The `KitchenSinkCoversAllProtocolTypes` test was silently failing with `Assert.NotNull() Failure` because the spec failed to load.
**Fix:** Removed the stray marker. Then ran `pwsh tools/format-specs.ps1` to also clean the empty `tags: []` field that lint caught.

### Test outcome

- **KitchenSink lint test:** ✅ Passes
- **pre-push full suite:** ✅ 1369 passed, 54 skipped, 0 failed

### Key learnings

- Always try `dotnet run --project src/BattleScribeSpec.Debugger -- {spec-id}` first when a KitchenSink test fails with a null spec — it immediately shows YAML parse errors.
- A single stray conflict marker (`>>>>>>>` without matching `<<<<<<<`) causes a cryptic YAML block scalar parse error. Git conflict scanning should be part of lint.
- `KitchenSinkProtocolTypeExclusions` should only contain `Protocol*`-prefixed types — other types cannot match the `StartsWith("Protocol")` filter used in `CheckKitchenSinkSetupTypeCoverage`.


**Task:** Implement `KitchenSinkCoversAllProtocolTypes` fact in `SpecLintTests.cs` per issue #197.

### What was implemented

Three coverage checks enforced on `protocol-kitchen-sink.yaml`:
1. **Protocol setup types** — reflection walk of all `Protocol*` concrete types in `BattleScribeSpec.Protocol` namespace; any type not instantiated in the spec's setup YAML fails.
2. **Action coverage** — all `KnownActions` except `dump` must appear in the spec's steps; missing actions fail.
3. **ExpectedState field coverage** — all non-nullable fields of `ExpectedStateDef`, `ExpectedForceDef`, `ExpectedSelectionDef` must be set in at least one `expectedState` step.

### Key learnings

- `ProtocolJsonContext` must be explicitly excluded — it starts with "Protocol", is concrete, but is a source-generated JSON context class, not a data type.
- `ProtocolSerializer` is abstract (static class in IL) — already excluded by `!t.IsAbstract`.
- `TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` → IDE0011 (braces required) and IDE0055 (formatting) are compile errors. Every `if`/`foreach` needs full braces.
- Running `dotnet test` without `-p:TestProfile=lint` causes `SpecLoader.FindRosterSpecsDirectory()` to return null → `NullReferenceException` in other lint tests. Always use `-p:TestProfile=lint` or `pre-push`.
- `SelectionCount` exists on both `ExpectedStateDef` and `ExpectedForceDef`. The check uses an OR: selectionCount must appear at roster OR force level (not required at both).

### Test outcome

392 existing lint tests still pass. New test fails with exactly 2 violations (Bobbie's pending additions):
- `[action] 'duplicateForce' not exercised`
- `[ExpectedStateDef] field 'Name' never set`

**Status:** ✅ Complete. Committed on branch `alex/kitchen-sink-lint-test`.
## Session 2026-05-12: PR #209 Review Rebase

**Date:** 2026-05-12T20:19:43+02:00

Participated in PR #209 (kitchen-sink protocol coverage) comprehensive review session with Copilot CLI.

- Addressed 4 review comment threads across 3 agents
- Rebased squad/197 onto origin/main
- Merged 2 inbox decisions into decisions.md
- Pre-push tests: 1369/0 (passing)

See .squad/log/2026-05-12T20-19-43-pr209-review-rebase.md for details.

---

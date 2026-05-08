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
  - #191 → #17 (DevEx)
  - #196 → #17 (DevEx)
  - #197 → #109 (Fair Use / Divergence)
  - #198 → #181 (Protocol Type System)

**Epic names discovered:**
- #16: NR/BS error handling divergence
- #17: Structured validation error model
- #109: NewRecruit automated testing & robots.txt compliance
- #181: JSON Schema/numeric precision

**Status:** ✅ Complete. All 6 links verified on GitHub.

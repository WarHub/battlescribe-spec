# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, YAML specs, IKVM (Java interop), Playwright, NUnit
- **Created:** 2026-05-08

## Learnings

### Project Board Structure (2026-05-08)
- **WarHub/battlescribe-spec uses GitHub Project Board v2 (#2)** for tracking conformance spec work across 32 open issues
- **Label taxonomy is coherent:** Priority tiers (backlog/high/medium) + Area ownership (spec-coverage/framework/newrecruit/devex) + Special flags (needs-design, bug)
- **Backlog split:** 56% backlog (long-term), 22% high (ready next), 9% medium/untagged
- **Area breakdown:** 53% spec-coverage (17 issues), 19% framework (6), 16% newrecruit (5), 6% devex (2)
- **Issue types:** 38% features, 25% other (untyped), 16% epics, 16% tasks, 6% bugs
- **Authentication blocker:** gh token lacks `read:project` scope; blocks Copilot CLI automation of board queries. Must regenerate token with full scopes.
- **Recent triage gap:** 3 recent issues (#199, #197, #196) lack priority/area labels. Must tag within 1 day.
- **Design blocker:** Epic #18 (Data Editor) is high priority but marked `needs-design`; unblock first before implementation.
- **Recommendation:** Regenerate gh token immediately, triage untagged issues today, resolve #18 design question within 3 days.

### Issue Triage (2026-05-08)
- **#199 & #197 are duplicates:** Both address kitchen-sink protocol coverage. #197 is canonical (more detailed context). #199 closed as duplicate.
- **Labels applied:**
  - #197: `area: spec-coverage` + `priority: high` (protocol coverage verification is essential, blocks PR #195)
  - #187: `area: spec-coverage` + `priority: medium` (constraint spec bug, non-blocking)
  - #186: `area: framework` + `priority: high` (adapter error reporting divergence, correctness issue)
  - #199: closed as duplicate of #197
- **Duplicate detection:** Identified that #199 was a simpler restatement of #197; consolidated to single issue with richer detail.

### Unparented Issue Audit & Work Order (2026-05-08)
- **Critical finding:** All 26 non-epic issues (100% of active work) are unparented. GitHub project board v2 has no parent field enforcement.
### Unparented Issue Audit & Work Order (2026-05-08) — Pre-Parentage Snapshot
- **Critical finding (at time of audit):** All 26 non-epic issues (100% of active work) were unparented. GitHub project board v2 has no parent field enforcement.
- **Epic coverage:** 5 epics identified (#18, #15, #16, #109, #181). Only 5 issues tracked; remaining 26 orphaned.
- **Design blocker:** #19 (scope decision) gates 6 downstream issues. Resolve within 3 days.
- **Parentage recommendations:** 23 issues can be parented to epics immediately:
  - #20–#25 → #15 (Preloaded roster)
  - #177, #187 → #16 (Error validation)
  - #88–#89 → #109 (Fair use policy)
  - #198, #159 → #181 (Protocol types)
- **Independent issues (high priority):** #197, #186, #196 remain unparented (cross-cutting).
- **Prioritized work order:** 2-week sprint structure:
  - **Phase 1 (Days 1–3):** Resolve #19 design blocker (unblock data editor epic).
  - **Phase 2 (Days 4–14):** Execute 7 high-priority features (#170–#173, #168–#169) + critical bugs (#197, #186).
  - **Phase 3 (Week 2+):** Backlog infrastructure & compliance work.
- **Effort estimate:** ~65 effort-days across 5-person team; feasible in 2 weeks with parallel streams.
- **Next checkpoint:** 2026-05-10 (post-design decision). Full work order documented in `.squad/decisions/inbox/avasarala-work-order.md`.

### Issue #19 Scope Decision — Split & Execution (2026-05-08)
- **Decision:** Split #19 into two issues per dual-epic structure (roster loading → #15, data editor → #18)
- **Issues created:** #201 (roster loading protocol), #202 (data editor MVP)
- **Scope selections:** Holden's Option 2 (LoadRosterCommand + SaveRosterCommand), Bobbie's 13-spec MVP
- **Key approvals:** Amadeusz Sadowski signed off on split; no design changes needed
- **Timeline:** Roster loading independent; data editor MVP ~6 weeks
- **Team ownership:** Holden (protocol), Bobbie (spec authoring), Miller/Johnson (adapters)

<!-- Append new learnings below. Each entry is something lasting about the project. -->

## Work Completed: Unparented Issue Triage (2026-05-08)

**Task:** Audit all non-epic issues and recommend parent epic assignments.
- **Finding:** 14 unparented non-epic issues (reduced from original 26 estimate after Alex's prior linking)
- **Triage scope:** All 14 issues analyzed and mapped to epics by domain
- **Distribution:**
  - #15 (Preloaded Roster Lifecycle): 1 issue (#41)
  - #16 (Error Validation): 1 issue (#186)
  - #17 (DevEx): 2 issues (#191, #196)
  - #18 (Data Editor): 9 issues (#168-174, #30, #31)
  - #109 (Fair Use / Divergence): 1 issue (#197)

**Status:** ✅ Complete. Decision record merged to decisions.md. Ready for link application.

**Note:** Triage audit confirmed epic name corrections:
- #16 is error validation coverage (NR/BS divergence handling)
- #17 is developer experience (tooling, debugging, automation)
- #18 is data editor conformance (all subsystems)

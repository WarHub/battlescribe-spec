# Issue Hierarchy

Full epic and sub-issue tree as of 2026-04-17.

## Open Epics

### Epic #73: Spec framework and tooling
Goal: Improve spec infrastructure, runner, loader, and authoring experience.

| # | Type | Title | Priority | Status |
|---|------|-------|----------|--------|
| #36 | Task | Validate ID uniqueness within spec setups | high | Open |
| #62 | Feature | Support step IDs and outputs for spec actions | medium, needs-design | Open |
| #65 | Feature | Add runner/adapter support for filtering specs by tags | medium | Open |
| #66 | Feature | Create JSON Schema for spec YAML format | medium | Open |
| #103 | Task | Name NR test rosters after their spec ID | medium | Open |
| #104 | Task | Show roster editor UI during non-headless NR runs | low | Open |
| #105 | Task | Investigate NR test performance optimizations | medium | Open |
| #106 | Task | Clean up NR test rosters to prevent accumulation | high | Open |
| #76 | Task | Protocol adherence smoke test spec | — | ✅ Closed |
| #81 | Task | Agent skills for common repo tasks | — | ✅ Closed |

### Epic #74: Developer experience and code quality
Goal: Reduce contributor friction, improve maintainability, optimize workflow.

| # | Type | Title | Priority | Status |
|---|------|-------|----------|--------|
| #34 | Task | Remove explicit default values from spec YAML files | high | Open |
| #43 | Feature | Evaluate ID-based engine/protocol APIs | low, needs-design | Open |
| #72 | Feature | Reduce DTO and mapping code (source generators) | low | Open |
| #42 | Task | Move datasource downloads into .testdata | low | Open |
| #35 | Task | Evaluate performance improvement opportunities | low | Open |

### Epic #16: Error validation coverage expansion
Goal: Increase coverage of error scenarios across assertions and action failures.

| # | Type | Title | Priority | Status |
|---|------|-------|----------|--------|
| #25 | Task | Add negative mutation error matrix specs | medium | Open |
| #24 | Feature | Expand assertion primitives for error checks | — | ✅ Closed |

### Epic #15: Preloaded roster lifecycle conformance
Goal: Add conformance coverage for tests beginning from existing roster state.

| # | Type | Title | Priority | Status |
|---|------|-------|----------|--------|
| #19 | Feature | Decide scope for roster loading and editor specs | backlog, needs-design | Open |
| #20 | Feature | Add preloaded roster setup to spec model | backlog | Open |
| #21 | Feature | Add protocol support for loading roster state | backlog | Open |
| #22 | Task | Add happy-path preloaded roster specs | backlog | Open |
| #23 | Task | Add malformed/invalid roster load specs | backlog | Open |
| #41 | Feature | Add specs for roster file import/export | backlog | Open |

### Epic #18: Data editor conformance specification
Goal: Define and test conformance for BattleScribe data editor operations.

| # | Type | Title | Priority | Status |
|---|------|-------|----------|--------|
| #29 | Feature | Define data editor conformance API surface | backlog, needs-design | Open |
| #30 | Task | Add editor mutation and round-trip specs | backlog | Open |
| #31 | Task | Add editor negative and validation specs | backlog | Open |

## Standalone issues (no parent epic)

| # | Type | Title | Priority | Labels |
|---|------|-------|----------|--------|
| #93 | Task | Expand Spec: cost limit negative other than -1 | high | area: spec-coverage |
| #92 | Task | Expand Spec: Child Forces | high | area: spec-coverage |
| #89 | Task | Set User-Agent header for NR automated access | medium | area: newrecruit |
| #88 | Task | Legal review: NR automated testing compliance | medium | area: newrecruit |

## Closed Epics

### Epic #17: Structured entry-linked validation errors ✅
All sub-issues completed: #26, #27, #28.

## Closed standalone issues
- #12 — Code Review: 22 issues identified (closed same day, items triaged)
- #8 — Multi-Engine Conformance Suite with NR Support ✅
- #37 — Analyze and spec default mechanisms ✅
- #39 — Frozen NR testing via HAR replay ✅
- #69 — Fill protocol type gaps ✅
- #77 — Make 'from' required on error assertions ✅

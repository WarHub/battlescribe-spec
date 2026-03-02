# New Recruit Conformance Baseline

Generated from `new-recruit-research` branch.

## Summary

| Metric | Count |
|--------|-------|
| Total specs | 222 |
| Synthetic specs (inline data) | 217 |
| Real-world specs (dataSource) | 5 |
| Passed | 0 |
| Expected failures | 5 |
| Skipped (no dataSource) | 217 |
| Unexpected failures | 0 |

## Architecture

The NR adapter uses Playwright to control the New Recruit web app (newrecruit.eu).

**Current capabilities:**
- ✅ Connect to live NR site via Playwright Chromium
- ✅ Select game system from NR's library via UI click
- ✅ Create roster via Pinia store API (`createRoster`, `insertForce`, `addList`)
- ✅ Read roster state via `window.__bsspec` global reference
- ✅ Execute actions (AddForce, SelectEntry, DeselectSelection, etc.)
- ✅ Read validation errors
- ✅ Shared browser fixture (1 browser for all 222 tests, ~56s total)

**Current limitations:**
- ❌ Cannot load synthetic inline data (no CatXml → NR loading pipeline)
- ❌ DataSource resolver not implemented (cannot fetch BSData repos)
- ❌ Real-world specs fail because `dataSource` URIs aren't resolved to game system data

## Real-World Spec Results (5 specs)

All 5 real-world specs use `dataSource: "github:BSData/wh40k-10e@v10.14.0"` and `engines: [newrecruit]`.
All fail because the DataSource resolver is not yet implemented — Setup receives empty game system data.

| Spec | Status | Failure Reason |
|------|--------|----------------|
| wh40k-10e-create-army | Expected Failure | No forces created (empty game system) |
| wh40k-10e-captain | Expected Failure | No forces created (empty game system) |
| wh40k-10e-multi-unit | Expected Failure | No forces created (empty game system) |
| wh40k-10e-points-cost | Expected Failure | No forces created (empty game system) |
| wh40k-10e-space-marines-intercessors | Expected Failure | Setup produces roster from wrong game system (default AoS 4.0 instead of W40k 10e) |

### Interesting: `wh40k-10e-space-marines-intercessors`

This spec partially works — Setup created a roster but from the wrong game system:
- Expected: "Strike Force" → Got: "General's Handbook 2025-26"
- Expected: "Intercessor Squad" → Got: "Allow Legends"

This suggests the adapter fell through to clicking the first available system label
(AoS 4.0) instead of finding Warhammer 40k 10e, because the spec's GameSystem name
wasn't populated from the dataSource.

## Synthetic Spec Results (217 specs)

All 217 synthetic specs are **skipped** for the NR engine. These use inline YAML definitions
for game systems and catalogues which cannot be loaded into NR's web mode.

**To make these work, one of these approaches is needed:**
1. **DataSource + real BSData repos** — resolve `dataSource` URIs to real game data
2. **CatXml injection** — generate BattleScribe XML from inline specs and inject via NR's import flow
3. **Local NR instance** — run NR locally with custom data loading

## Categories Breakdown

| Category | Total | Applicable | Status |
|----------|-------|------------|--------|
| condition | 29 | 0 (all inline) | Skipped |
| constraint | 20 | 0 (all inline) | Skipped |
| cost | 8 | 0 (all inline) | Skipped |
| force | 9 | 0 (all inline) | Skipped |
| modifier | 23 | 0 (all inline) | Skipped |
| refresh | 3 | 0 (all inline) | Skipped |
| roster | 11 | 0 (all inline) | Skipped |
| scope | 9 | 0 (all inline) | Skipped |
| selection | 47 | 0 (all inline) | Skipped |
| validation | 58 | 0 (all inline) | Skipped |
| real-world | 5 | 5 | Expected Failure |

## Next Steps

1. **Implement DataSource resolver** — fetch and cache BSData repos via git clone
2. **Wire DataSource into SpecRunner** — resolve `dataSource` URIs before calling Setup
3. **Map game system names** — ensure the NR adapter finds the right system in NR's library
4. **Expand real-world specs** — add more wh40k-10e scenarios with proper assertions
5. **Consider name-based entry lookup** — for real data, entry indices are unstable

# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, Playwright, Pinia, NewRecruitRosterEngine.cs, NewRecruitActions.cs, HarRecorder.cs
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- HAR allowlist: newrecruit.eu, raw.githubusercontent.com, Google Fonts only — all others stripped
- Pinia store IDs: 'systemsStore' for systems, 'lists' for lists/rosters; army at `window.__bsspec.army` after setup
- NR composite IDs: `getBattleScribePath()` returns `::` entryId; `getBattleScribePath(true)` returns `::` entryGroupId; `getId()` is plain target ID
- Collective behavior: `source.collective`, `source.collective_recursive`, `getModelAmount()` — selection amount stays 1, costs multiply
- Instanced: `isInstanced: true` → `addInstance()` (separate nodes); `false` → `setAmount({}, getAmount() + (getStep() ?? 1))`; NR deleted `incrementAmount`/`decrementAmount` in v35.72
- costIndex population is required for NR adapter initialization
- Game-system-only specs (no catalogues) must skip nr-editor — NR adapter requires at least one catalogue
- NR engine name: "nr-editor"; BS engine name: "battlescribe"

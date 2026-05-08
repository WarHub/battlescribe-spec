# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, IKVM (Java interop), BattleScribeEngine.cs, JavaModelFactory.cs, BattleScribeRosterEngine.cs
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- JavaModelFactory uses `setImported/isImported` (not `setImport`); field name in specs is "imported"
- Java engine private method `v()` must be called explicitly after `x()` (auto-select); `b()` (selectEntry) triggers it internally
- Composite entry IDs: `FindEntryById` can match full composite IDs (split on `::`) or individual segment
- BS engine: `net.battlescribe.engine.a.f` class contains the private `v()` validation method

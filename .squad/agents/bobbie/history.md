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

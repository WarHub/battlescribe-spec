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

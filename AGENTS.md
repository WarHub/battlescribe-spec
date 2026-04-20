# AGENTS.md

BattleScribe Spec — declarative conformance test suite for BattleScribe roster engines.
YAML specs in `specs/` define setup, actions, and expected state. SpecRunner executes them.

## Project status: Experimental

This project is in an **experimental stage**. All interfaces, formats, conventions, and
architecture are subject to change without notice. There is **no backward compatibility
guarantee** — breaking changes are not just allowed but actively encouraged when they
improve architecture, code quality, or reduce tech debt. Prefer bold restructuring over
incremental workarounds. When in doubt, choose the cleaner design.

## Build & test

```bash
dotnet restore && dotnet build    # first time
dotnet test                       # all tests
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~my-spec-id"  # one spec
```

## After editing specs

```bash
pwsh -File tools/format-specs.ps1                                                  # auto-fix formatting
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~SpecLint"    # verify lint
```

## Key files

| Path | What |
|------|------|
| `specs/{category}/{id}.yaml` | Spec files (302 total, 16 categories) |
| `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` | All Protocol setup types |
| `src/BattleScribeSpec.TestKit/EngineTypes.cs` | State records (Roster/Force/Selection/Profile/Rule/Category/Cost) |
| `src/BattleScribeSpec.TestKit/SpecFileModels.cs` | YAML spec model classes |
| `src/BattleScribeSpec.TestKit/SpecRunner.cs` | Assertion engine |
| `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` | Action dispatch |
| `tests/Infrastructure/SpecLintTests.cs` | Lint rules, known tags |
| `tools/format-specs.ps1` | Spec formatter |


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
dotnet restore && dotnet build                                                     # first time
dotnet test -p:TestProfile=pre-push                                                # lint + Oracle + NR frozen (~40s)
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~my-spec-id"  # one spec
```

**Always run `pre-push` before pushing.** It covers lint, Oracle conformance, and NR frozen
(offline HAR replay) in one fast command. Other profiles: `lint`, `oracle`, `nr-frozen`,
`nr-live`, `nr-live-visible`.

## Debugging specs

Use `bs-spec-debug` to run a spec step-by-step and inspect full roster state:

```bash
dotnet run --project src/BattleScribeSpec.Debugger -- selection-publication        # by spec ID
dotnet run --project src/BattleScribeSpec.Debugger -- --dump protocol/kitchen-sink # dump after every step
dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr --json spec.yaml # NR engine, JSON output
```

Options: `--dump` (all steps), `--json`, `--engine oracle|nr`, `--no-headless`.
Specs can include `action: dump` steps for explicit dump points.

## After editing specs

```bash
pwsh -File tools/format-specs.ps1                                                  # auto-fix formatting
```

## Key files

| Path | What |
|------|------|
| `specs/{category}/{id}.yaml` | Spec files (309 total, 17 categories) |
| `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` | All Protocol setup types |
| `src/BattleScribeSpec.TestKit/EngineTypes.cs` | State records (Roster/Force/Selection/Profile/Rule/Category/Cost) |
| `src/BattleScribeSpec.TestKit/SpecFileModels.cs` | YAML spec model classes |
| `src/BattleScribeSpec.TestKit/SpecRunner.cs` | Assertion engine + dump callback |
| `src/BattleScribeSpec.TestKit/StateDumper.cs` | Pretty-print roster state (tree or JSON) |
| `src/BattleScribeSpec.TestKit/IDumpEnricher.cs` | Engine-specific dump enrichment interface |
| `src/BattleScribeSpec.Debugger/Program.cs` | bs-spec-debug console app |
| `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` | Action dispatch |
| `tests/Infrastructure/SpecLintTests.cs` | Lint rules, known tags |
| `tools/format-specs.ps1` | Spec formatter |


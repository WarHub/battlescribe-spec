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
dotnet test -p:TestProfile=pre-push                                                # lint + BS + NR frozen (~40s)
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~my-spec-id"  # one spec
```

**Always run `pre-push` before pushing.** It covers lint, BattleScribe conformance, and NR frozen
(offline HAR replay) in one fast command. Other profiles: `lint`, `bs`, `nr-frozen`,
`nr-live`, `nr-live-visible`.

## NR frozen tests and HAR

The frozen NR tests replay a **single HAR snapshot of the entire NR web application** (JS
bundles, CSS, assets). This is NOT per-spec — all specs run against the same HAR. Adding or
editing specs requires no HAR changes; new specs work immediately. The HAR is versioned by
NR client version (pinned in `testdata.json`), updated separately via
[WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) releases.

## Debugging specs

Use `bs-spec-debug` to run a spec step-by-step and inspect full roster state:

```bash
dotnet run --project src/BattleScribeSpec.Debugger -- selection-publication        # by spec ID
dotnet run --project src/BattleScribeSpec.Debugger -- --dump protocol/kitchen-sink # dump after every step
dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr --json spec.yaml # NR engine, JSON output
dotnet run --project src/BattleScribeSpec.Debugger -- --export-xml ./out/ cost/cost-hidden-limit-validation
```

Options: `--dump` (all steps), `--json`, `--engine bs|nr`, `--no-headless`,
`--export-xml <dir>` (generate .gst/.cat XML files from spec setup and exit).
Specs can include `action: dump` steps for explicit dump points.

## After editing specs

```bash
pwsh -File tools/format-specs.ps1                                                  # auto-fix formatting
```

## Key files

| Path | What |
|------|------|
| `specs/roster/{category}/{id}.yaml` | Roster spec files (312 total, 17 categories) |
| `specs/gamedata/{category}/{id}.yaml` | GameData spec files (10 total, 1 category) |
| `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` | All Protocol setup types |
| `src/BattleScribeSpec.TestKit/Roster/RosterTypes.cs` | Roster state records |
| `src/BattleScribeSpec.TestKit/Roster/RosterSpecModels.cs` | Roster YAML spec model classes |
| `src/BattleScribeSpec.TestKit/Roster/RosterRunner.cs` | Roster assertion engine + dump callback |
| `src/BattleScribeSpec.TestKit/GameData/IGameDataEngine.cs` | GameData engine interface |
| `src/BattleScribeSpec.TestKit/GameData/GameDataTypes.cs` | GameData state records |
| `src/BattleScribeSpec.TestKit/GameData/GameDataSpecModels.cs` | GameData YAML spec model classes |
| `src/BattleScribeSpec.TestKit/GameData/GameDataRunner.cs` | GameData assertion engine |
| `src/BattleScribeSpec.TestKit/GameData/MemoryGameDataEngine.cs` | In-memory GameData engine for testing |
| `src/BattleScribeSpec.Debugger/Program.cs` | bs-spec-debug console app |
| `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` | Action dispatch |
| `tests/Infrastructure/SpecLintTests.cs` | Roster lint rules, known tags |
| `tests/Infrastructure/GameDataSpecLintTests.cs` | GameData lint rules |
| `tools/format-specs.ps1` | Spec formatter |


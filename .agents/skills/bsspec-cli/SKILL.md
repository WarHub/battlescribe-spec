---
name: bsspec-cli
description: >
  Drive the bs-spec CLI to run, inspect, and debug conformance specs against any engine.
  Use when running a spec to see full roster/gamedata state, probing a live UI (BattleScribe
  desktop or NewRecruit browser) to discover selectors or debug behavior, pausing mid-run to
  evaluate JS, reading a UI driver's diagnostics after a CI failure, or extending a UI driver.
  Covers the run/probe/export-xml/format verbs, the engine model (product × --ui × domain),
  and per-engine probe/diagnostics references for BS Roster UI, NR Roster UI, BS GameData UI,
  and NR GameData UI.
---

# bs-spec CLI

`bs-spec` is the developer CLI for the conformance suite. One spec, any engine: run it and
dump state, open the real app for inspection, generate XML, or format specs. It replaces the
old per-engine probe scripts — there is one tool with four verbs.

```bash
# Invoke via dotnet run (what you type day-to-day):
dotnet run --project src/BattleScribeSpec.Cli -- <verb> [args]
# …or the built binary directly:
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll <verb> [args]
```

| Verb | Purpose |
|------|---------|
| `run <spec>` | Execute a spec end-to-end against an engine; report pass/fail and dump state. |
| `probe <spec>` | Open a `--ui` engine with the spec's data loaded for interactive inspection. No assertions. |
| `export-xml <spec> <dir>` | Generate BattleScribe `.gst`/`.cat` XML from a spec's setup. No engine. |
| `format [<dir>] [--check]` | Format roster spec YAML in place (or report drift with `--check`). |

> **Rule: use the CLI, not ad-hoc xUnit test files, for debugging.** `probe` and `run --break`
> give a live session with proper spec data loaded — and a JS REPL where the engine supports
> one — without leaving throwaway test files behind.

## Engine selection — three orthogonal axes

The engine is chosen by three independent axes, so you never memorize packed names:

- **Product** — `--engine battlescribe` (default) or `--engine newrecruit`.
- **Surface** — add `--ui` to drive the real desktop app / browser; omit it for the
  in-process / API engine.
- **Domain** — roster vs gamedata, **inferred from the spec path** (`specs/gamedata/…` →
  gamedata). Force it with `--gamedata` / `--roster` when piping via stdin or when the path
  is ambiguous.

All eight combinations exist:

| Domain | `--engine battlescribe` | `--engine battlescribe --ui` | `--engine newrecruit` | `--engine newrecruit --ui` |
|--------|-------------------------|------------------------------|-----------------------|----------------------------|
| roster | in-process IKVM (reference) | BattleScribe Roster Editor (Java agent) | Playwright, Pinia direct | NewRecruit browser, real clicks |
| gamedata | in-process data editor | BattleScribe Data Editor (Java agent) | frozen static files | NR Editor browser |

UI engines assert as their non-UI counterpart (battlescribe-ui uses battlescribe's assertion
overrides), since they drive the same product.

## `run` — execute a spec

```bash
dotnet run --project src/BattleScribeSpec.Cli -- run selection/selection-page          # by id
dotnet run --project src/BattleScribeSpec.Cli -- run --all-steps --json spec.yaml       # per-step JSON
dotnet run --project src/BattleScribeSpec.Cli -- run --engine newrecruit selection-page # NR (frozen HAR)
cat spec.yaml | dotnet run --project src/BattleScribeSpec.Cli -- run -                   # from stdin
```

`<spec>` accepts a file path, a `category/id`, a bare `id`, or `-` (stdin). By default state
is dumped after the **last** step; assertions run and a pass/fail summary prints to stderr
(the state dump goes to stdout, so `--json` output pipes cleanly).

| Flag | Effect |
|------|--------|
| `--all-steps` | Dump state after every step, not just the last. |
| `-o {tree,json}` / `--json` | State dump format (`--json` = `-o json`). |
| `--headed` | Show the browser/app window (UI engines; default headless). |
| `--break <n>` | Pause before step *n* and drop into a REPL / inspection prompt. |
| `--screenshots <dir>` | Save a PNG after each step (UI engines). |
| `--timeline <file>` | Write a self-contained HTML timeline report (screenshots embedded for UI engines). |
| `--record <file>` | Record UI actions to JSON (battlescribe-ui). |
| `--save-roster <dir>` | Save the final roster as `.ros` XML (battlescribe-ui). |
| `--keep-alive` | Keep the BattleScribe app running between runs (battlescribe-ui). |

**Uniform capability handling:** every artifact flag is accepted for every engine. If the
chosen engine can't honor one (e.g. `--screenshots` on the in-process engine), the CLI prints
one `warning: … skipping` line rather than silently doing nothing.

## `probe` — interactive inspection

```bash
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui my-spec-id
```

`probe` requires `--ui` — it exists to open the real app. It loads the spec's data, dumps the
initial UI state, and (for engines that support evaluation) drops into a REPL. No spec steps
run. Use it to discover selectors, verify assumptions, and watch behavior by hand.

- **NewRecruit** (roster or gamedata) opens a browser and a **JS REPL** against the live page.
- **BattleScribe** (roster or gamedata) opens the JavaFX app and dumps the scene graph; there
  is **no REPL** — inspect via the scene dump, screenshots, and diagnostic files.

The NR REPL is drivable non-interactively for DOM discovery:
`echo '<js>' | dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui <spec>`.

## `--break <n>` — pause mid-run

Runs the spec up to step *n-1*, then pauses before step *n*. For NR UI it opens the same JS
REPL against the live page; for other engines it waits for Enter (inspect diagnostics /
screenshots, then continue). Step indices are 0-based, matching `--all-steps` output.

## Which engine, which reference

| Working on… | Engine flags | Reference |
|-------------|--------------|-----------|
| BS Roster Editor UI: scene graph, agent RPC, screenshots, KeepAlive | `--engine battlescribe --ui` (roster) | [BS-ROSTER-UI.md](references/BS-ROSTER-UI.md) |
| NR roster behavior: Pinia/DOM, JS REPL, selection mechanics | `--engine newrecruit --ui` (roster) | [NR-ROSTER-UI.md](references/NR-ROSTER-UI.md), [NR-INTERNALS.md](references/NR-INTERNALS.md) |
| BS Data Editor gamedata driver (Java agent) | `--engine battlescribe --ui` (gamedata) | [BS-GAMEDATA-UI.md](references/BS-GAMEDATA-UI.md) |
| NR Editor gamedata driver (Playwright) | `--engine newrecruit --ui` (gamedata) | [NR-GAMEDATA-UI.md](references/NR-GAMEDATA-UI.md) |

## Diagnostics — check these first on a CI failure

Each UI driver auto-captures a diagnostic bundle when an action fails. Download/inspect these
before reproducing locally:

| Engine | Directory | Contents |
|--------|-----------|----------|
| BS Roster UI | `artifacts/bs-ui-diagnostics/` | scene graph, all-windows dump, thread dump, screenshot, stack trace |
| NR Roster UI | `artifacts/nr-ui-diagnostics/` | screenshot, console log, DOM snapshot, Pinia state |
| BS GameData UI | `artifacts/bs-gamedata-ui-diagnostics/` | scene + thread dump, data-state JSON, screenshot |
| NR GameData UI | `artifacts/nr-gamedata-ui-diagnostics/` | screenshot, console log, editorStore JSON, DOM |

## Environment setup

Run `setup.ps1` once: it downloads BattleScribe + the Liberica JDK, builds the Java agent jar,
and downloads the NR Editor frozen static files and Playwright browsers. Everything is then
auto-discovered — no env vars needed. Optional overrides: `BS_UI_JAVA_PATH`, `BS_UI_APP_DIR`,
`BS_UI_AGENT_JAR` (BS UI artifacts); `NR_ENGINE_URL` (live NR roster, default
`https://www.newrecruit.eu`); `NR_EDITOR_URL` (live NR Editor).

## Reference files

- [BS-ROSTER-UI.md](references/BS-ROSTER-UI.md) — BattleScribe Roster Editor UI: probe sequence,
  scene-graph format, `BsUiProbe`/`BsUiDiagnostics`/`AgentClient` APIs, startup, KeepAlive.
- [NR-ROSTER-UI.md](references/NR-ROSTER-UI.md) — NewRecruit roster UI: probe REPL,
  `NrUiProbe`/`NrUiDiagnostics` APIs, common Pinia/DOM JS snippets, architecture.
- [NR-INTERNALS.md](references/NR-INTERNALS.md) — deobfuscated NR behaviors: selection
  mechanics (`addInstance`/`incrementAmount`/`autocheck`), `setAmount` corruption traps,
  publication resolution, hidden-cost handling.
- [BS-GAMEDATA-UI.md](references/BS-GAMEDATA-UI.md) — BS Data Editor gamedata driver: Java agent
  RPC routing, edit-panel control-id map, how to extend `DataEditorActions.java`.
- [NR-GAMEDATA-UI.md](references/NR-GAMEDATA-UI.md) — NR Editor gamedata driver: tree/context-menu
  selectors and traps, frozen vs live mode, how to extend `NrGameDataUiActions.cs`.

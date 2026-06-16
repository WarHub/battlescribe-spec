---
name: bs-ui-probing
description: >
  Debug BattleScribe UI driver actions and test failures. Use when investigating BS UI
  test failures, reading scene graph dumps, interpreting BsUiDiagnostics output, or
  discovering JavaFX CSS selectors for new agent actions. Covers the debugger probe
  workflow (--probe, --stop-before, --screenshots, --record), BsUiDiagnostics artifacts
  for CI failure inspection, and required environment setup.
---

# BS UI Probing

Inspect BS UI driver behavior using the `bs-spec-debug` debugger's probe mode.
The probe stages game data, launches the BattleScribe desktop app with the Java agent,
dumps the initial scene graph and window list to stdout, then waits for you to inspect.

## Environment setup

Three env vars must be set before the BS UI engine can run:

```powershell
$env:BS_UI_JAVA_PATH    = "C:\path\to\bs-jre\bin\java.exe"   # JavaFX-capable JRE
$env:BS_UI_APP_DIR      = "C:\path\to\battlescribe\app"       # dir with RosterEditor.jar
$env:BS_UI_AGENT_JAR    = "C:\path\to\bs-ui-java-agent.jar"  # built agent jar
```

Conventional locations (auto-resolved if env vars absent):

```
lib/battlescribe/jre-win/bin/java.exe   (Windows)
lib/battlescribe/jre-mac/bin/java       (macOS)
lib/battlescribe/jre/bin/java           (Linux)
lib/battlescribe/RosterEditor.jar
src/bs-ui-java-agent/bs-ui-java-agent.jar
```

`setup.ps1` downloads BattleScribe to `lib/battlescribe/`.
The agent JAR must be built separately:
```powershell
pwsh -File src/bs-ui-java-agent/build.ps1
```

## Quick start — probe mode

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/battlescribe-ui --probe my-spec-id
```

Probe sequence:
1. Stages `.gst`/`.cat` XML files into an isolated BS home directory
2. Launches BattleScribe with the Java agent attached
3. Waits for the "Roster Editor" window (30s timeout)
4. Dismisses the startup "download data?" dialog
5. Dumps the **scene graph** (JSON) and **open windows** to stdout
6. Prints `Press Enter to shut down...` — BS remains running until you press Enter

Use this to see the initial UI state with your spec's game data loaded.

## Stop-before — inspect state mid-spec

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/battlescribe-ui --stop-before 5 my-spec-id
```

Runs the spec to step 4, then pauses before step 5:

```
═══ Stopped before step 5: selectEntry "Commander" ═══
Press Enter to continue execution, or Ctrl+C to abort...
```

The BattleScribe window remains open and visible. Inspect it manually, check the
`artifacts/bs-ui-diagnostics/` directory for the latest dump, then press Enter to
continue (or Ctrl+C to abort).

> **Note:** BS UI `--stop-before` does not provide a JSON-RPC REPL — there is no
> equivalent to NR's interactive JS REPL. Scene inspection is done by reading diagnostic
> files or via screenshots.

## Other useful flags

| Flag | Effect |
|------|--------|
| `--screenshots <dir>` | Save PNG screenshot after every step to `<dir>/` |
| `--record <path>` | Record UI interactions to a JSON file for later analysis |
| `--dump` | Print full roster state (from engine API) after every step |
| `--no-headless` | Keep the window visible after spec run (BS UI is always visible) |
| `--report <path>` | Write a timeline HTML report with per-step screenshots |

## BsUiDiagnostics — reading CI failures

When a BS UI action times out or throws, `BsUiDiagnostics.CaptureAsync()` writes a
diagnostic dump to `artifacts/bs-ui-diagnostics/<timestamp>_<spec-id>.txt`.

The dump contains:
- **Open windows** — all open `Stage` titles + dimensions
- **All windows scene dump** (depth=4) — scene graph across all windows
- **Thread dump** — all live JVM threads with stack traces (from `threadDump` RPC)
- **Scene graph** (depth=5) — focused window's full node tree
- **Stack trace** — the C# exception that triggered the capture

Check `artifacts/bs-ui-diagnostics/` after a CI failure. The thread dump reveals
deadlocks or hung FX-thread operations. The scene graph shows what state the UI was
actually in (e.g., unexpected modal dialog blocking the expected node).

Override the diagnostics directory:
```powershell
$env:BS_UI_DIAGNOSTICS_DIR = "D:\my-diags"
```

## Scene graph format

The `dumpTree` / `dumpAllWindows` commands return JSON representing the JavaFX
scene graph. Key fields on each node:

```json
{
  "type": "Button",          // JavaFX class simple name
  "id": "btnNewRoster",      // CSS #id (null if not set)
  "styleClass": ["button"],  // CSS classes (.class)
  "text": "New Roster",      // visible text (Label, Button, etc.)
  "visible": true,
  "disabled": false,
  "children": [ ... ]        // nested nodes
}
```

**CSS selectors** used in `findNode` / `fireButton` follow JavaFX CSS syntax:
- `#btnNewRoster` — by ID
- `.label` — by style class
- `Button` — by type name

## Common investigation patterns

### Check what window/dialog is blocking an action

```powershell
# Probe the spec, check scene dump output for unexpected modal stages
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/battlescribe-ui --stop-before 3 my-spec-id
# Look for: "modality": "APPLICATION_MODAL" in the dumpAllWindows output
```

### Capture screenshots at every step

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/battlescribe-ui --screenshots artifacts/steps my-spec-id
```

### Record UI interactions for new action discovery

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/battlescribe-ui --record artifacts/recorded-actions.json my-spec-id
```

## Design decision — no interactive REPL

Unlike the NR UI probe (which runs in a browser with full JS REPL access),
the BS UI probe interacts with a native JavaFX desktop app via JSON-RPC.
There is no equivalent interactive REPL in `--stop-before` mode — inspection
is done via the initial scene dump, saved screenshots, and diagnostic files.

For deeper investigation requiring live JSON-RPC calls, see the
`AgentClient` API in [BS-UI-PROBE.md](references/BS-UI-PROBE.md).

## Reference files

- [BS-UI-PROBE.md](references/BS-UI-PROBE.md) — BsUiProbe API, BsUiDiagnostics format,
  AgentClient JSON-RPC commands, environment resolution logic

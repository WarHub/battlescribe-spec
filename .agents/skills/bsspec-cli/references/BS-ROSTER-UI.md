# BattleScribe Roster Editor UI

Driving and debugging the BattleScribe desktop **Roster Editor** via the Java agent
(`--engine battlescribe --ui`, roster domain). The agent attaches to the JavaFX app and
exposes a JSON-RPC interface; the C# driver stages data, launches the app, and issues RPC
calls. See the parent [SKILL.md](../SKILL.md) for the CLI verbs and flags.

## Probe sequence

```bash
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine battlescribe --ui my-spec-id
```

1. Stages the spec's `.gst`/`.cat` XML into an isolated BattleScribe home directory.
2. Launches BattleScribe with the Java agent attached.
3. Waits for the "Roster Editor" window (30 s timeout).
4. Dismisses the startup "download data?" dialog.
5. Dumps the **scene graph** (JSON) and **open windows** to stdout.
6. Prints `Press Enter to shut down…` — the app stays up until you press Enter.

There is **no interactive REPL** (unlike the NR browser probe). Inspect via the initial scene
dump, `--screenshots`, and the diagnostic files below. For live JSON-RPC calls, drive the
`AgentClient` API from your own tooling.

## Mid-run inspection

`run --engine battlescribe --ui --break N my-spec-id` runs to step *N-1*, then pauses with the
window open and visible:

```
═══ Stopped before step 5: selectEntry "Commander" ═══
Press Enter to continue execution, or Ctrl+C to abort…
```

Inspect the window, read the latest `artifacts/bs-ui-diagnostics/` dump, then press Enter.

A common pattern: pause before the failing step and look for an unexpected modal dialog
(`"modality": "APPLICATION_MODAL"` in the all-windows scene dump) blocking the expected node.

## Scene graph format

`dumpTree` / `dumpAllWindows` return JSON for the JavaFX scene graph. Each node:

```json
{
  "type": "Button",          // JavaFX class simple name
  "id": "btnNewRoster",      // CSS #id (null if unset)
  "styleClass": ["button"],  // CSS classes (.class)
  "text": "New Roster",      // visible text
  "visible": true,
  "disabled": false,
  "children": [ ... ]
}
```

CSS selectors (used by `findNode` / `fireButton`) follow JavaFX CSS syntax: `#btnNewRoster`
(id), `.label` (style class), `Button` (type name).

## `BsUiProbe` (C# class)

`src/BattleScribeSpec.BsRosterUiDriver/BsUiProbe.cs`. Reached via `probe`; instantiable
directly for custom probe scripts.

| Method | Signature | Description |
|--------|-----------|-------------|
| `LaunchAsync` | `(ProtocolGameSystem, IReadOnlyList<ProtocolCatalogue>, IReadOnlyList<(string, string)>, TextWriter?)` | Stage data files, launch BS, connect, dismiss startup dialogs |
| `DumpTreeAsync` | `(TextWriter, int maxDepth=15)` | Dump the active window's scene graph |
| `DumpWindowsAsync` | `(TextWriter)` | List all open windows |
| `Client` | `AgentClient` property | Direct JSON-RPC client for custom calls |

`LaunchAsync` takes `xmlFiles` as `(FileName, Content)` pairs:

```csharp
var xmlFiles = new List<(string, string)>
{
    ("system.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem))
};
foreach (var (name, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
    xmlFiles.Add((name, xml));
```

## `BsUiDiagnostics` (static class)

`src/BattleScribeSpec.BsRosterUiDriver/BsUiDiagnostics.cs`. Called automatically by
`BsUiRosterEngine` on action failure:

```csharp
string? dumpPath = await BsUiDiagnostics.CaptureAsync(
    client, specId: "my-spec", actionDescription: "SelectEntry Commander", failure: exception);
```

Output dir `artifacts/bs-ui-diagnostics/` (override `BS_UI_DIAGNOSTICS_DIR`); file
`<yyyyMMdd-HHmmss-fff>_<spec-id>.txt`. Structure:

```
═══ BS UI DRIVER DIAGNOSTIC DUMP ═══
Timestamp / Spec / Action / Error Type / Error
─── OPEN WINDOWS ───            all Stage titles + dimensions
─── ALL WINDOWS SCENE DUMP ───  scene graph across all windows (depth 4)
─── THREAD DUMP ───             all JVM threads + stack traces
─── SCENE GRAPH (depth=5) ───   focused window's node tree
─── STACK TRACE ───             the C# exception that triggered capture
```

Diagnosing common failures:
- `"modality": "APPLICATION_MODAL"` in Open Windows → unexpected dialog blocking actions.
- FX thread `"state": "WAITING"` in the thread dump → FX-thread deadlock (async fireButton).
- Empty scene graph → BS crashed or the window closed unexpectedly.

## `AgentClient` — JSON-RPC commands

`src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs`. Typed methods:

| C# Method | RPC | Use |
|-----------|-----|-----|
| `PingAsync()` | `ping` | Liveness check |
| `GetWindowsAsync()` | `getWindows` | List open windows |
| `DumpTreeAsync(depth)` | `dumpTree` | Scene graph of active window |
| `FireButtonAsync(selector, windowTitle)` | `fireButton` | Click a button by CSS selector |
| `CallAsync(method, params)` | any | Raw RPC call |

Raw `CallAsync` unlocks more probe/diagnostic commands:

```csharp
// All windows at once — find unexpected modals
await client.CallAsync("dumpAllWindows", new JsonObject { ["maxDepth"] = 4 });
// JVM thread states — when BS appears hung
await client.CallAsync("threadDump", null);
// Screenshot (result["png"] = base64 PNG)
await client.CallAsync("captureScreenshot", new JsonObject { ["windowTitle"] = "Roster Editor" });
// Find a node by CSS selector or visible text
await client.CallAsync("findNode", new JsonObject { ["selector"] = "#btnNewRoster", ["windowTitle"] = "Roster Editor" });
await client.CallAsync("findNodeByText", new JsonObject { ["text"] = "New Roster", ["nodeType"] = "Button" });
// Visible roster state read from the UI (independent of the engine API)
await client.CallAsync("getUiState", null);  // { rosterName, forces, costs }
```

## Startup sequence & isolated home

```
BsRosterApp.StartAsync()
 → java -javaagent:"bs-ui-java-agent.jar" -Xms1024m "-Duser.home=<isolated>" -jar "RosterEditor.jar"
 → reads "BSUI_AGENT_PORT=<port>" from stdout (30 s timeout)
 → AgentClient.ConnectAsync() — TCP 127.0.0.1:<port> → PingAsync()
 → WaitForWindowAsync("Roster Editor", 30 s)
 → HandleStartupDialogsAsync() — dismiss the "download data?" dialog
```

Isolated home (created under temp): `<temp>/<guid>/BattleScribe/{data/<gameSystemId>/ (staged
.gst/.cat + index.bsi), rosters/, settings/ (settings.xml suppresses the download popup)}`.

## Warm start (KeepAlive)

`BsUiRosterEngine.KeepAlive = true` preserves the running process between spec runs. On the next
`Setup()`: ping the agent → if alive, close the current roster and re-stage data; if the ping
fails, kill the old process and cold-start. Saves the ~5–10 s JVM startup during iteration. The
CLI sets KeepAlive internally for `run --engine battlescribe --ui`.

## Environment resolution

`setup.ps1` provisions everything; the CLI auto-discovers:
- **Java**: repo-local Liberica JDK at `lib/liberica-jdk/bin/java[.exe]` (`BsUiPaths.ResolveJavaPath`).
- **App**: `lib/battlescribe/RosterEditor.jar`.
- **Agent**: `src/bs-ui-java-agent/bs-ui-java-agent.jar` (built by `setup.ps1`).

Overrides (non-default artifacts only): `BS_UI_JAVA_PATH` (takes precedence over the Liberica
JDK), `BS_UI_APP_DIR` (directory containing `RosterEditor.jar`), `BS_UI_AGENT_JAR`.

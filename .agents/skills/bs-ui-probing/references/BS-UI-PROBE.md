# BS UI Probe — Harness API & Agent Reference

## BsUiProbe (C# class)

Located in `src/BattleScribeSpec.BsRosterUiDriver/BsUiProbe.cs`.

**Intended use:** accessed via the debugger's `--probe` flag. Direct C# instantiation
is possible for custom probe scripts.

### Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `LaunchAsync` | `(ProtocolGameSystem, IReadOnlyList<ProtocolCatalogue>, IReadOnlyList<(string, string)>, TextWriter?)` | Stage data files, launch BS, connect, dismiss startup dialogs |
| `DumpTreeAsync` | `(TextWriter, int maxDepth=15)` | Dump scene graph of the active window |
| `DumpWindowsAsync` | `(TextWriter)` | List all open windows |
| `Client` | `AgentClient` property | Direct access to JSON-RPC client for custom calls |

`LaunchAsync` takes `xmlFiles` as `(FileName, Content)` pairs — generate them with:
```csharp
var xmlFiles = new List<(string, string)>
{
    ("system.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem))
};
foreach (var (name, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
    xmlFiles.Add((name, xml));
```

## BsUiDiagnostics (static class)

Located in `src/BattleScribeSpec.BsRosterUiDriver/BsUiDiagnostics.cs`.

```csharp
// Called automatically by BsUiRosterEngine on action failure:
string? dumpPath = await BsUiDiagnostics.CaptureAsync(
    client,
    specId: "my-spec",
    actionDescription: "SelectEntry Commander",
    failure: exception);
```

Output directory: `artifacts/bs-ui-diagnostics/` (override via `BS_UI_DIAGNOSTICS_DIR`).
File naming: `<yyyyMMdd-HHmmss-fff>_<spec-id>.txt`

### Dump file structure

```
═══════════════════════════════════════════════════════════
  BS UI DRIVER DIAGNOSTIC DUMP
═══════════════════════════════════════════════════════════

Timestamp:  2026-05-10T12:34:56.789Z
Spec:       my-spec-id
Action:     SelectEntry Commander
Error Type: TimeoutException
Error:      Timed out after 10s waiting for node #btnAdd

─── OPEN WINDOWS ───────────────────────────────────────
[{ "type": "Stage", "title": "Roster Editor", "showing": true, ... }]

─── ALL WINDOWS SCENE DUMP (depth=4) ────────────────────
{ "windowCount": 2, "windows": [...] }

─── THREAD DUMP ────────────────────────────────────────
{ "threads": [{ "name": "JavaFX Application Thread", "state": "WAITING", "stack": [...] }] }

─── SCENE GRAPH (depth=5) ──────────────────────────────
{ "windowTitle": "Roster Editor", "tree": {...} }

─── STACK TRACE ────────────────────────────────────────
System.TimeoutException: Timed out after 10s...
```

**Diagnosing common failures:**
- `"modality": "APPLICATION_MODAL"` in Open Windows → unexpected dialog blocking actions
- Thread state `"WAITING"` on FX thread → FX thread deadlock (fireButton async issue)
- Empty scene graph → BS crashed or window closed unexpectedly

## AgentClient — key JSON-RPC commands

Located in `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs`.

The `AgentClient` wraps JSON-RPC calls. Typed methods available:

| C# Method | RPC Method | Use |
|-----------|------------|-----|
| `PingAsync()` | `ping` | Liveness check |
| `GetWindowsAsync()` | `getWindows` | List open windows |
| `DumpTreeAsync(depth)` | `dumpTree` | Scene graph of active window |
| `FireButtonAsync(selector, windowTitle)` | `fireButton` | Click a button by CSS selector |
| `CallAsync(method, params)` | any | Raw RPC call |

For probe/diagnostic use, the raw `CallAsync` unlocks additional commands:

### `dumpAllWindows`

```csharp
var result = await client.CallAsync("dumpAllWindows", new JsonObject { ["maxDepth"] = 4 });
```

Returns scene graphs for ALL open windows simultaneously. Essential for identifying
unexpected modal dialogs.

### `threadDump`

```csharp
var result = await client.CallAsync("threadDump", null);
```

Returns all JVM thread states with stack traces. Use when BS appears hung.

### `captureScreenshot`

```csharp
var result = await client.CallAsync("captureScreenshot", new JsonObject { ["windowTitle"] = "Roster Editor" });
// result["png"] = base64 PNG
```

### `findNode` / `findNodeByText`

```csharp
// By CSS selector
var node = await client.CallAsync("findNode", new JsonObject
{
    ["selector"] = "#btnNewRoster",
    ["windowTitle"] = "Roster Editor"
});

// By visible text
var node2 = await client.CallAsync("findNodeByText", new JsonObject
{
    ["text"] = "New Roster",
    ["nodeType"] = "Button"
});
```

### `getUiState`

```csharp
var state = await client.CallAsync("getUiState", null);
// Returns: { "rosterName": "My Army", "forces": [...], "costs": [...] }
```

Reads the visible roster state from the UI — roster name from window title, forces from
the tree view, costs from visible labels. Independent of the engine API.

## Environment resolution

The debugger resolves BS UI paths in this order:

1. **Env vars** (explicit override):
   - `BS_UI_JAVA_PATH` — path to `java(.exe)` binary
   - `BS_UI_APP_DIR` — directory containing `RosterEditor.jar`
   - `BS_UI_AGENT_JAR` — path to `bs-ui-java-agent.jar`

2. **Conventional repo locations** (fallback):
   - `lib/battlescribe/jre-win/bin/java.exe` (Windows)
   - `lib/battlescribe/jre-mac/bin/java` (macOS)
   - `lib/battlescribe/jre/bin/java` (Linux)
   - `lib/battlescribe/RosterEditor.jar`
   - `src/bs-ui-java-agent/bs-ui-java-agent.jar`

`setup.ps1` downloads BattleScribe to `lib/battlescribe/`. The agent JAR is **not**
included — build it separately:
```powershell
pwsh -File src/bs-ui-java-agent/build.ps1
```

## Startup sequence details

```
BsRosterApp.StartAsync()
  ↓
java -javaagent:"bs-ui-java-agent.jar" -Xms1024m "-Duser.home=<isolated>" -jar "RosterEditor.jar"
  ↓ (stdout)
BSUI_AGENT_PORT=<port>           ← C# reads this line (30s timeout)
  ↓
AgentClient.ConnectAsync()       ← TCP to 127.0.0.1:<port>
  ↓
client.PingAsync()               ← confirms connectivity
  ↓
WaitForWindowAsync("Roster Editor", 30s)
  ↓
HandleStartupDialogsAsync()      ← dismisses "download data?" Confirm dialog
```

**Isolated home directory** (created automatically under temp):
```
<temp>/<guid>/
  BattleScribe/
    data/<gameSystemId>/    ← staged .gst and .cat files + index.bsi
    rosters/
    settings/
      settings.xml          ← prevents download popup
      repositories.xml
```

## Warm start (KeepAlive)

Set `BsUiRosterEngine.KeepAlive = true` to preserve the running BS process between
spec runs. On subsequent `Setup()` calls:
1. Pings agent — if alive, closes current roster and re-stages data
2. If ping fails, kills old process and cold-starts

Useful for iterative debugging where JVM startup time (~5-10s) is significant.
The debugger uses KeepAlive internally when running with `--engine roster/battlescribe-ui`.

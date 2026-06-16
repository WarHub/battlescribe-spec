# NR UI Probe — Harness API & Debugger Reference

## NrUiProbe (C# class)

Located in `src/BattleScribeSpec.NrRosterUiDriver/NrUiProbe.cs`.

**Intended use:** accessed via the debugger's `--probe` flag. Direct C# instantiation
is possible but not the recommended workflow.

### Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `LaunchAsync` | `(ProtocolGameSystem, IReadOnlyList<ProtocolCatalogue>, string, TextWriter?)` | Launch visible browser, load spec data |
| `LaunchFrozenAsync` | `(string harFilePath, ..., TextWriter?)` | Launch in HAR replay mode |
| `EvalAsync<T>` | `(string expression) → Task<T>` | Evaluate JS, deserialize as T |
| `EvalStringAsync` | `(string expression) → Task<string?>` | Evaluate JS, return string |
| `ScreenshotAsync` | `(string path) → Task` | Capture full-page screenshot |
| `RunReplAsync` | `(TextReader input, TextWriter output) → Task` | Interactive REPL loop |

`LaunchAsync` loads spec game data via the NR UI's "Add From Folder" flow (using a
`showDirectoryPicker` mock). The roster is NOT created on launch — data is loaded only.

## Debugger flags for NR probing

All flags used with `--engine roster/newrecruit-ui`.

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine roster/newrecruit-ui [flags] <spec-id>
```

| Flag | Description |
|------|-------------|
| `--probe` | Launch probe mode: load data, open visible browser + REPL. No spec steps run. |
| `--stop-before N` | Run spec up to step N-1, then open REPL before executing step N. Continue with `exit`. |
| `--no-headless` | Keep browser visible after spec run (for post-run inspection). |
| `--dump` | Print full roster state after every step. |
| `--dump <prefix>` | Print roster state at every explicit `action: dump` step. |

### `--probe` mode

Calls `RunNrUiProbe(spec, headless: false)` internally:
1. Parses spec `setup` block to get `(gameSystem, catalogues)`
2. Calls `NrUiProbe.LaunchAsync(...)` — browser opens, data loaded
3. Calls `NrUiProbe.RunReplAsync(Console.In, Console.Out)` — REPL active

Use `NR_ENGINE_URL` env var to target a specific NR instance (default: `https://newrecruit.eu`).

### `--stop-before N` mode

Registered via `runner.OnBeforeStep`. At step N:
1. Prints `═══ Stopped before step N: <description> ═══`
2. Drops into a JS REPL using `nrUiEngine.EvaluateAsync<JsonElement>(line)`
3. Continues execution after `exit` or `quit`

Step indices are 0-based (same as `--dump` output).

## NrUiDiagnostics (C# class)

Located in `src/BattleScribeSpec.NrRosterUiDriver/NrUiDiagnostics.cs`.

Used automatically by the NR UI driver on action failures. Not normally called directly.

### DiagnosticReport

```csharp
sealed record DiagnosticReport(
    byte[]? Screenshot,        // PNG bytes, null if capture failed
    IReadOnlyList<string> ConsoleLog,  // "[type] message" per console event
    string? DomSnapshot,       // body.outerHTML, truncated to 5 KB
    string? PiniaState)        // JSON: { forces, maxCosts }
{
    string FormatText()  // Human-readable text summary (non-binary fields)
}
```

Artifacts are saved to `artifacts/nr-ui-diagnostics/` on test failure.

### Pinia state shape

```json
{
  "forces": [
    {
      "uid": "force-uid-string",
      "name": "Battalion",
      "selections": [
        {
          "uid": "sel-uid-string",
          "name": "Commander",
          "amount": 1,
          "entryId": "entry-id-from-catalogue"
        }
      ]
    }
  ],
  "maxCosts": [
    { "typeId": "pts", "value": 1000 }
  ]
}
```

## Common JS snippets for the REPL

### Get Pinia stores

```javascript
const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
const lists = pinia._s.get('lists')
const systems = pinia._s.get('systemsStore')
const user = pinia._s.get('userStore')
```

### Get current army state

```javascript
const army = lists.currentList?.army
army?.getForces?.()?.length
army?.getForces?.()?.map(f => ({ name: f.getName(), uid: f.uid }))
```

### Walk all selections in a force

```javascript
const f = army.getForces()[0]
f.getSelections().map(s => ({
  name: s.getName(), uid: s.uid, amount: s.getAmount(),
  children: s.getSelections().length
}))
```

### Inspect available entry selectors (what can be added)

```javascript
const f = army.getForces()[0]
// Selectors are uninstantiated templates — the entries that can be added
const sels = f.selectors?.map(s => ({ id: s.getId(), name: s.getName?.() }))
```

### Bypass supporter paywall

```javascript
user.user = { supporter: true, name: 'Test', _id: 'fake' }
user.isSupporter() // → true
```

### Check current page URL / navigation state

```javascript
window.location.href
document.querySelector('.router-link-active')?.textContent
```

### Get cost totals (including hidden cost types)

```javascript
function sumNode(node, result) {
    for (const sel of (node.getSelections?.() || [])) {
        const amount = sel.getAmount?.() ?? 0
        if (amount <= 0) continue
        for (const c of (sel.getCosts?.() || []))
            result[c.typeId] = (result[c.typeId] || 0) + c.value * amount
        sumNode(sel, result)
    }
}
const totals = {}
for (const f of army.getForces()) sumNode(f, totals)
totals
```

### Check constraint errors

```javascript
army.checkConstraints().map(e => ({ msg: e.msg, field: e.constraint?.field }))
```

## Architecture

```
NrUiProbe
  ↓ LaunchAsync / LaunchFrozenAsync
NrRosterUiEngine.CreateAsync / CreateFrozenAsync
  ↓
NewRecruitBrowser (Playwright, HAR replay)
  ↓
NrUiSetup.LoadGameDataAsync (injects showDirectoryPicker mock → "Add From Folder")
  ↓
NrUiProbe.RunReplAsync ← REPL reads from Console.In
```

Data reading uses `NewRecruitStateReader` (reads Pinia directly via JS) —
only mutations go through the UI. This is the same design as the non-UI NR adapter.

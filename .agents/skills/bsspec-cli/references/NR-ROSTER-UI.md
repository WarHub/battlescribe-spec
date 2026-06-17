# NewRecruit Roster UI

Probing and debugging the live **NewRecruit** roster app via Playwright
(`--engine newrecruit --ui`, roster domain). Mutations go through the real UI; state is read
from the Pinia stores via injected JS. See the parent [SKILL.md](../SKILL.md) for the CLI verbs
and flags, and [NR-INTERNALS.md](NR-INTERNALS.md) for the deobfuscated engine behaviors.

## Probe REPL

```bash
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui my-spec-id
# Target a specific instance (default https://newrecruit.eu):
NR_ENGINE_URL=https://newrecruit.eu dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui my-spec-id
```

The browser opens (visible), the spec's data is loaded via the "Add From Folder" flow, and a
JS REPL accepts expressions:

```
NR UI probe ready. Browser is open.
Entering REPL — type JS expressions to evaluate, 'exit' to quit:
> document.title
"NewRecruit"
> const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
> pinia._s.get('lists')?.currentList?.army?.getForces?.()?.length
0
```

`run --engine newrecruit --ui --break N` opens the **same REPL** before step *N*, with the
spec run up to that point. Type `exit`/`quit` to continue.

## `NrUiProbe` (C# class)

`src/BattleScribeSpec.NrRosterUiDriver/NrUiProbe.cs`. Reached via `probe`.

| Method | Signature | Description |
|--------|-----------|-------------|
| `LaunchAsync` | `(ProtocolGameSystem, IReadOnlyList<ProtocolCatalogue>, string, TextWriter?)` | Launch visible browser, load spec data |
| `LaunchFrozenAsync` | `(string harFilePath, …, TextWriter?)` | Launch in HAR replay mode |
| `EvalAsync<T>` | `(string) → Task<T>` | Evaluate JS, deserialize as T |
| `EvalStringAsync` | `(string) → Task<string?>` | Evaluate JS, return string |
| `ScreenshotAsync` | `(string path) → Task` | Full-page screenshot |
| `RunReplAsync` | `(TextReader, TextWriter) → Task` | Interactive REPL loop |

`LaunchAsync` loads game data via the NR UI's "Add From Folder" flow (a `showDirectoryPicker`
mock). The roster is **not** created on launch — data is loaded only.

Architecture: `NrUiProbe → NrRosterUiEngine.CreateAsync/CreateFrozenAsync → NewRecruitBrowser
(Playwright, HAR replay) → NrUiSetup.LoadGameDataAsync → RunReplAsync`. Reads use
`NewRecruitStateReader` (Pinia directly via JS); only mutations go through the UI — the same
design as the non-UI NR adapter.

## `NrUiDiagnostics`

`src/BattleScribeSpec.NrRosterUiDriver/NrUiDiagnostics.cs`. Captured automatically on action
failure to `artifacts/nr-ui-diagnostics/`. Check these before reproducing a CI failure locally.

```csharp
sealed record DiagnosticReport(
    byte[]? Screenshot,                // PNG, null if capture failed
    IReadOnlyList<string> ConsoleLog,  // "[type] message" per console event
    string? DomSnapshot,               // body.outerHTML, truncated to 5 KB
    string? PiniaState);               // JSON: { forces, maxCosts }
```

The Pinia dump is the most useful field:

```json
{
  "forces": [
    { "uid": "f-1", "name": "Battalion",
      "selections": [{ "uid": "s-1", "name": "Commander", "amount": 1, "entryId": "se-commander" }] }
  ],
  "maxCosts": [{ "typeId": "pts", "value": 1000 }]
}
```

## REPL JS snippets

```javascript
// Pinia stores
const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
const lists = pinia._s.get('lists'), user = pinia._s.get('userStore')

// Current army
const army = lists.currentList?.army
army?.getForces?.()?.map(f => ({ name: f.getName(), uid: f.uid }))

// Walk a force's selections
army.getForces()[0].getSelections().map(s => ({
  name: s.getName(), uid: s.uid, amount: s.getAmount(), children: s.getSelections().length }))

// Available entry selectors (what can be added) — uninstantiated templates
army.getForces()[0].selectors?.map(s => ({ id: s.getId(), name: s.getName?.() }))

// Constraint errors
army.checkConstraints().map(e => ({ msg: e.msg, field: e.constraint?.field }))

// Cost totals INCLUDING hidden cost types (calcTotalCosts omits hidden — see NR-INTERNALS.md)
function sumNode(node, result) {
  for (const sel of (node.getSelections?.() || [])) {
    if ((sel.getAmount?.() ?? 0) <= 0) continue
    for (const c of (sel.getCosts?.() || [])) result[c.typeId] = (result[c.typeId] || 0) + c.value * sel.getAmount()
    sumNode(sel, result)
  }
}
const totals = {}; for (const f of army.getForces()) sumNode(f, totals); totals

// Bypass the supporter paywall (custom name/notes are premium)
user.user = { supporter: true, name: 'Test', _id: 'fake' }; user.isSupporter()
```

For *why* these patterns are needed — selection mechanics, `setAmount` corruption traps,
publication-on-`.source`, hidden-cost handling — see [NR-INTERNALS.md](NR-INTERNALS.md).

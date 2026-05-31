---
name: nr-adhoc-probing
description: >
  Run ad-hoc probes against the live NewRecruit engine. Use when debugging NR behavior,
  verifying assumptions about NR internals, inspecting Pinia store state at a specific
  spec step, or discovering UI selectors. Covers the debugger probe workflow
  (--probe, --stop-before), NrUiDiagnostics for CI failure inspection, and NR
  Pinia/DOM JS patterns.
---

# NR Ad-Hoc Probing

Inspect NR behavior interactively using the `bs-spec-debug` debugger's probe mode.
The probe loads spec data into a live NR browser session and provides a JS REPL —
no temporary test files needed.

## Quick start — probe mode

```powershell
# Launch NR with spec data loaded, open a JS REPL
dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr-ui --probe my-spec-id

# Set NR_ENGINE_URL if probing live NR (default is https://newrecruit.eu)
$env:NR_ENGINE_URL = "https://newrecruit.eu"
```

The browser opens (visible), spec data is loaded, and the REPL accepts JS expressions:

```
NR UI probe ready. Browser is open.
Entering REPL — type JS expressions to evaluate, 'exit' to quit:
> document.title
"NewRecruit"
> const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
undefined
> pinia._s.get('lists')?.currentList?.army?.getForces?.()?.length
0
```

## Stop-before — inspect state mid-spec

```powershell
# Run spec, pause before step N, drop into REPL with full roster state
dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr-ui --stop-before 5 my-spec-id
```

Steps run in order; at step 5 the REPL opens before that step executes.
Evaluate any JS against the live NR page — same REPL as `--probe`.
Type `exit` or `quit` to continue execution.

## Other useful flags

| Flag | Effect |
|------|--------|
| `--no-headless` | Keep browser visible after the spec run |
| `--dump` | Print full roster state after every step |
| `--engine nr-ui` | Required — selects the NR UI driver |

## NrUiDiagnostics — reading CI failures

When an NR UI action times out or fails, `NrUiDiagnostics` captures:
- **Screenshot** (PNG) of the page at failure time
- **Browser console log** collected since test start
- **DOM snapshot** (body HTML, truncated to 5 KB)
- **Pinia state dump** (forces, selections, maxCosts as JSON)

Artifacts are saved to `artifacts/nr-ui-diagnostics/`. Check these first
when investigating a failing CI run before running locally.

The Pinia dump is particularly useful:

```json
{
  "forces": [
    {
      "uid": "f-1",
      "name": "Battalion",
      "selections": [
        { "uid": "s-1", "name": "Commander", "amount": 1, "entryId": "se-commander" }
      ]
    }
  ],
  "maxCosts": [{ "typeId": "pts", "value": 1000 }]
}
```

## Design decision — harness scope

The probe harness is `NrUiProbe` (C# class in `BattleScribeSpec.NrRosterUiDriver`),
accessed through the debugger's `--probe` and `--stop-before` flags. This avoids
temporary xUnit test files and gives the same JS REPL with proper spec data loaded.

**Rule:** Use the debugger probe, not ad-hoc xUnit test files, for NR debugging.

## Reference files

- [NR-INTERNALS.md](references/NR-INTERNALS.md) — Deobfuscated NR behaviors: Pinia access,
  selection mechanics, cost types, setAmount quirks, publication resolution
- [NR-UI-PROBE.md](references/NR-UI-PROBE.md) — NrUiProbe and NrUiDiagnostics API reference,
  all debugger flags, common JS snippets

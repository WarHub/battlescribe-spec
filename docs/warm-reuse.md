# Host-side warm engine reuse

`bs-engine-host` can keep **one engine alive across specs** instead of disposing and recreating it
for every spec. The engine is reset between specs with its existing `Cleanup()` (the same primitive
the in-process engine pool uses), and disposed once at process shutdown.

Without it, a host process that serves a whole batch pays a full engine cold start **per spec** —
for UI engines that means launching a browser or a JVM every time.

It is **opt-in per domain**, and — this is the important part — **it is only enabled where it has
been measured to be both correct and faster.**

## What is actually enabled

| Engine | Domain | Warm-reuse | Why |
|---|---|---|---|
| `battlescribe-ui` | **gamedata** | ✅ **enabled** | Verdicts identical to cold, **2.20× faster**. The cold cost is a JVM + JavaFX launch per spec, so reuse pays for itself. |
| `newrecruit-ui` | gamedata | ❌ cold | Correct (53/53 verdicts identical) but **0.92× — no benefit**. Headless Chromium relaunches in ~1.6s, about what NR's per-spec reset costs. |
| `newrecruit-ui` | roster | ❌ cold | **Broken.** 6/8 warm-only failures and 1.8× *slower*. See "NR-UI roster" below. |
| `battlescribe-ui` | roster | ❌ cold | The BattleScribe app **terminates itself** when kept alive. See "BS-UI roster" below. |
| `newrecruit` | both | ❌ cold | Not measured to benefit; same browser-relaunch economics as `newrecruit-ui`. |
| `battlescribe` | both | ⚪ n/a | In-process; engine construction is cheap. Nothing to save. |

**No engine warm-reuses the roster domain today.**

## Measurements

All runs `--workers 1`, warm vs cold on the identical spec set, via `scripts/bench-warm-reuse.ps1`.
The harness **asserts that per-spec PASS/FAIL verdicts are identical** between warm and cold — a
speedup that changes conformance results is not a speedup, it's a bug.

| Engine / domain | Specs | Warm | Cold | Speedup | Verdicts |
|---|---:|---:|---:|---:|---|
| `battlescribe-ui` gamedata | 54 | **159.7s** | 350.7s | **2.20×** | ✅ all identical |
| `battlescribe-ui` gamedata | 8 | 44.9s | 69.8s | 1.56× | ✅ all identical |
| `newrecruit-ui` gamedata | 53 | 95.2s | 87.6s | 0.92× | ✅ all identical |
| `newrecruit-ui` roster | 8 | 258.8s | 145.0s | 0.56× | ❌ **6 mismatches** |

The speedup grows with batch size for `battlescribe-ui` gamedata (1.56× at 8 specs → 2.20× at 54),
because the per-spec JVM cold start is what's being amortized away.

### The premise that didn't survive contact with data

This work began on the assumption that NewRecruit warm-reuse would be the big win — "~80 NR specs
each cold-starting Chromium." **That turned out to be false.** A headless Chromium relaunch is
cheap (~1.6s), and NR's own per-spec reset (store teardown + navigation) costs about the same, so
warm-reuse buys NR nothing. The real win was the engine nobody expected: the BattleScribe **Data
Editor**, where the cold cost is a JVM + JavaFX startup.

Measure before you optimize — and measure *correctness*, not just wall time.

## Why long warm sessions are safe now

Warm-reuse keeps a single connection alive across hundreds of specs, which exposed a latent bug in
**both** protocol layers: responses were correlated **positionally** (write a request, read the next
line). When a client-side timeout fired, the in-flight response arrived late and was consumed as the
answer to the *next* request — permanently shifting the stream and cascading failures through the
rest of the batch.

Both layers now correlate responses **by id**, via a dedicated reader loop that discards late or
abandoned responses:

- **NDJSON adapter protocol** (`AdapterProcess`) — optional `corrId` on commands, echoed by
  `AdapterHandler`. Adapters that don't echo it fall back to strict positional ordering.
- **JSON-RPC agent protocol** (`AgentClient`) — the JSON-RPC `id` was already sent and echoed; it
  simply wasn't checked.

The **timeout hierarchy** was also inverted and is now fixed: the CLI's per-request timeout (3 min)
must exceed any host-side operation (BS-UI actions can legitimately run ~122s), so a timeout means
"the adapter is genuinely dead," never "the adapter is still working."

The BS-UI Java agent additionally **reports unexpected modal dialogs** (with the dialog's text and a
full-display screenshot) instead of blocking until timeout — a modal that no action expects is a
failure, by definition.

## Known limitations

### BS-UI roster — the app kills itself

Kept alive, the BattleScribe Roster Editor dies (exit -1, with native `hs_err` crash dumps). Its
stderr shows a background `TimerThread` polling `https://battlescribe.net/rest/sponsormessage/getMessages`.
Cold never trips this because each JVM only lives ~6s. Measured: warm **4/102** specs pass with the
host process dying, vs cold **17/18**. The crash cause has not yet been confirmed from the dump
itself — the phone-home is the leading suspect but is not yet proven.

### NR-UI roster — the shared browser isn't reset

Only the first roster-creating spec of a warm batch passes; every later one times out in `addForce`
waiting on NR's Create List dropdown. `NrRosterUiEngine.Cleanup` doesn't fully clear the previous
list, so the leftover row makes the dialog's controls ambiguous — the exact hazard its own code
comment warns about. Cleanup was changed to delete lists through NR's own `listsStore.deleteList`
API (rather than splicing the array, which never told NR to delete anything), but that alone did not
fix it; diagnosis is hampered by the host's stderr being swallowed (issue #303).

CI never caught this because the NR-UI roster lane runs a **single** spec.

## Reproducing

```powershell
# Warm vs cold, with a verdict-equality assertion
pwsh -File scripts/bench-warm-reuse.ps1 -Engine battlescribe-ui -Domain gamedata -Filter "entry/,export/"
```

Force cold for any engine (ablation / diagnosis):

```bash
BSSPEC_DISABLE_WARM_REUSE=1 bs-spec run --all --engine battlescribe-ui --gamedata
```

## Related

- **#303** — `AdapterProcess` buffers the engine host's stderr instead of forwarding it, so host-side
  diagnostics are invisible during a run. This actively obstructed the NR-UI roster diagnosis above.
- **#304** — `SpecSuiteRunner` has no recovery when a pooled adapter process dies; one crash fails
  every remaining spec on that worker.

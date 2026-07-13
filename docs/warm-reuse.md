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
| `battlescribe-ui` | **gamedata** | ✅ **enabled** | Verdicts identical to cold, **2.20× faster**. |
| `battlescribe-ui` | **roster** | ✅ **enabled** | Verdicts identical to cold, **1.79× faster**. |
| `newrecruit-ui` | gamedata | ❌ cold | Verdicts identical, but **0.92× — no benefit**. |
| `newrecruit-ui` | roster | ❌ cold | Warm-reuse was **0.56× and produced wrong verdicts**. |
| `newrecruit` | both | ❌ cold | Same economics as `newrecruit-ui`. |
| `battlescribe` | both | ⚪ n/a | In-process; engine construction is cheap. Nothing to save. |

For NewRecruit the answer is **not** warm-reuse — it is **`--workers N`** (3.8× at 4 workers, verdicts
identical). Each spec already gets an isolated `BrowserContext` on a shared browser, so parallelism
is safe. See "NewRecruit: browser reuse is the wrong lever" below.

Both BattleScribe UI domains pay off for the same reason: their cold cost is a **JVM + JavaFX launch
per spec**. Neither NewRecruit domain does, because a headless Chromium relaunch is cheap (~1.6s).

## Measurements

All runs `--workers 1`, warm vs cold on the identical spec set, via `bs-spec compare` (see
"Reproducing" below).
The harness **asserts that per-spec PASS/FAIL verdicts are identical** between warm and cold — a
speedup that changes conformance results is not a speedup, it's a bug.

| Engine / domain | Specs | Warm | Cold | Speedup | Verdicts |
|---|---:|---:|---:|---:|---|
| `battlescribe-ui` gamedata | 54 | **159.7s** | 350.7s | **2.20×** | ✅ all identical |
| `battlescribe-ui` roster | 42 | **189.8s** | 340.5s | **1.79×** | ✅ all identical |
| `battlescribe-ui` gamedata | 8 | 44.9s | 69.8s | 1.56× | ✅ all identical |
| `newrecruit-ui` gamedata | 53 | 95.2s | 87.6s | 0.92× | ✅ all identical |
| `newrecruit-ui` roster | 8 | 258.8s | 145.0s | 0.56× | ❌ **6 mismatches** |

The speedup grows with batch size for `battlescribe-ui` gamedata (1.56× at 8 specs → 2.20× at 54),
because the per-spec JVM cold start is what's being amortized away.

### The premise that didn't survive contact with data

This work began on the assumption that NewRecruit warm-reuse would be the big win — "~80 NR specs
each cold-starting Chromium." **That turned out to be exactly backwards.** A headless Chromium
relaunch is cheap (~1.6s) and NR's own per-spec reset costs about the same, so warm-reuse buys NR
nothing — and on NR roster it actively corrupts results. The real win was the pair nobody was
looking at: **both BattleScribe UI domains**, where the cold cost is a JVM + JavaFX startup.

Two lessons, both learned the hard way here:

1. **Measure before you optimize.** The engine we built this for gains nothing.
2. **Measure *correctness*, not just wall time.** NR-UI roster warm-reuse was shipped "verified"
   because the browser launched once instead of N times — the mechanism worked. Nobody checked
   whether the *results* were still right. They weren't. That is why this harness asserts
   verdict-equality, and why it is the harness, not the stopwatch, that decides what ships.

## NewRecruit: browser reuse is the wrong lever — parallelism is the right one

NR sessions no longer launch their own Chromium. A single browser is launched **per process**
(`NrBrowserHost`) and every session gets its own **`BrowserContext`** — a private storage partition
(cookies, localStorage, IndexedDB, cache, service workers) with a fresh page, hence a fresh JS heap
and fresh Pinia stores. Specs therefore **cannot** leak state into one another, and there is no
scrubbing code to get wrong. This mirrors `NewRecruitEnginePool`, which already ran N engines as N
contexts of one browser in-process.

Two measurements settle how NR should actually be sped up (8 roster specs):

| Approach | Wall | vs sequential | Verdicts |
|---|---:|---:|---|
| Sequential (`--workers 1`) | 143.7s | — | 7 pass / 1 fail |
| **Parallel (`--workers 4`)** | **37.8s** | **3.8× faster** | ✅ identical |
| Engine/page warm-reuse | 258.8s | 0.56× (*slower*) | ❌ 6 wrong |

**Sharing the browser saved nothing** (143.7s vs a 145.0s per-spec-browser baseline) — launching
Chromium was never the cost; an NR spec spends its ~18s on SPA boot, HAR routing, game-data load and
UI actions. It is kept anyway because it makes isolation *structural*, which is what makes parallel
contexts safe and what killed the leftover-list corruption for good.

So the engines split cleanly by where their cost lives:

- **BattleScribe UI** — expensive cold start (JVM + JavaFX), cannot parallelize (`MaxParallel = 1`)
  → **warm-reuse** wins (1.79–2.20×).
- **NewRecruit** — cheap cold start, parallelizes freely → **`--workers N`** wins (3.8× at 4), and
  warm-reuse is worthless.

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

### BS-UI — the app can intermittently self-terminate

Kept alive long enough, the BattleScribe app has been observed to die (exit -1, with native `hs_err`
crash dumps); its stderr showed a background `TimerThread` polling
`https://battlescribe.net/rest/sponsormessage/getMessages`. This is **intermittent** — a clean 42-spec
warm roster benchmark reproduced no crash at all, and the crash cause has never been confirmed from
a dump.

Warm-reuse is still enabled because it is measured correct and ~1.8–2.2× faster. Mitigations:
`BsUiRosterEngine` self-heals (poisons itself and cold-restarts the app) on an unexpected modal or a
timeout, so an engine-level failure costs one restart rather than corrupting later specs. The gap is
that a **host-process** death still fails the rest of that worker's batch — see **#304**. Suppressing
the app's phone-home from the Java agent (we hold `Instrumentation` and run before its `main`) is the
obvious next step if the crash proves recurrent.

### NR-UI roster warm-reuse (historical)

Warm-reuse of the NR roster engine used to corrupt results: only the first roster-creating spec
of a batch passed, because `Cleanup` never fully cleared the previous list and the leftover row
made NR's Create List dropdown ambiguous. **This class of bug is now structurally impossible** —
each spec gets its own `BrowserContext` (see above), so there is no shared state to leak. NR
warm-reuse remains disabled anyway, because it buys nothing: use `--workers N` instead.

CI never caught the original bug because the NR-UI roster lane runs a **single** spec.

## Reproducing

### ⚠️ BROKEN ABLATION LEVER — Temporarily Unavailable

The recipe below is **documented but does not work** — a dangerous state:

```bash
# BROKEN — This example no longer works. Do not use.
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --config-a "" --config-b "BSSPEC_DISABLE_WARM_REUSE=1"
```

**Why it is broken:** `BSSPEC_DISABLE_WARM_REUSE` is dead code — nothing in the harness reads it anymore. The variable was deleted when warm-reuse moved to a `ConcurrencyPolicy` computed and passed by the parent. Following this documented recipe today:

1. Injects an environment variable nobody reads
2. Runs **both arms warm** (identical configurations)
3. Reports "verdicts identical, 1.00× speedup"
4. **Produces a false green** that reads exactly like "warm-reuse is verdict-safe — confirmed"

The lever is disconnected while the gauge still says PASS. This is worse than no documentation at all.

### The Working Replacement (Not Yet Available)

The correct way to ablate warm-reuse will be:

```bash
bs-spec compare --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --policy-a "reuse=on" --policy-b "reuse=off"
```

**Status: NOT YET IMPLEMENTED.** This flag is being wired up in Tasks 5–6 of `docs/superpowers/plans/2026-07-13-harness-concurrency-model.md`. Do not use the flag yet — it does not exist.

### Measurements (Still Valid)

The recorded verdicts and speedups (2.20× gamedata, 1.79× roster, verdicts identical) were produced with a working lever and remain valid. It is only the *reproduction instructions* that are broken — do not follow the recipe above.

### Diagnosis (Temporary Workaround)

For now, there is no user-facing knob to force cold. Internal diagnosis can temporarily modify `src/BattleScribeSpec.EngineHost/HostEngineFactory.cs` to force `KeepAlive = false`, rebuild, and run — then revert and rebuild. This is not a documented path; use only for urgent investigation.

## Related

- **#303 (fixed)** — `AdapterProcess` used to buffer the engine host's stderr instead of forwarding
  it, so host-side diagnostics were invisible during a run (this actively obstructed the NR-UI
  roster diagnosis above). Fixed by commit `3a564a0` ("closes #303"): stderr lines are now also
  forwarded live to the parent's stderr, tagged with the worker index.
- **#304** — `SpecSuiteRunner` has no recovery when a pooled adapter process dies; one crash fails
  every remaining spec on that worker.

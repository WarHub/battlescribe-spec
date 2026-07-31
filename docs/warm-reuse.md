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

For NewRecruit the answer is **not** warm-reuse — it is **parallelism** (3.8× at 4 workers, verdicts
identical; the worker count is now chosen by `ConcurrencyPolicy`, and forced only for an ablation via
`--policy workers=N`). Each spec already gets an isolated `BrowserContext` on a shared browser, so
parallelism is safe. See "NewRecruit: browser reuse is the wrong lever" below.

Both BattleScribe UI domains pay off for the same reason: their cold cost is a **JVM + JavaFX launch
per spec**. Neither NewRecruit domain does, because a headless Chromium relaunch is cheap (~1.6s).

## Measurements

All runs single-worker (`--policy workers=1`, which is also what the policy picks for
`battlescribe-ui`: its `MaxParallel` is 1), warm vs cold on the identical spec set, via
`bs-spec compare` (see "Reproducing" below).
The harness **asserts that per-spec PASS/FAIL verdicts are identical** between warm and cold — a
speedup that changes conformance results is not a speedup, it's a bug.

| Engine / domain | Specs | Warm | Cold | Speedup | Verdicts |
|---|---:|---:|---:|---:|---|
| `battlescribe-ui` gamedata | 54 | **159.7s** | 350.7s | **2.20×** | ✅ all identical |
| `battlescribe-ui` roster | 42 | **189.8s** | 340.5s | **1.79×** | ✅ all identical |
| `battlescribe-ui` gamedata | 8 | 44.9s | 69.8s | 1.56× | ✅ all identical |
| `newrecruit-ui` gamedata | 53 | 95.2s | 87.6s | 0.92× | ✅ all identical |
| `newrecruit-ui` roster | 8 | 258.8s | 145.0s | 0.56× | ❌ **6 mismatches** |
| `newrecruit-ui` roster (re-measured 2026-07-31) | 8 | — | — | **not reportable** | ❌ **7 mismatches** |

The 2026-07-31 row re-ran the ablation after #336 fixed `Cleanup`'s list deletion, since the
reuse-policy table named the leftover list row as the cause. It is still broken, for a different
reason than the one on record — see "NR-UI roster warm-reuse" under Known limitations. No timing is
quoted because `compare` refuses to report one when verdicts differ, and quoting a wall-clock ratio
from a run that got 7 specs wrong is precisely the mistake this table exists to prevent.

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
| Sequential (1 worker) | 143.7s | — | 7 pass / 1 fail |
| **Parallel (4 workers)** | **37.8s** | **3.8× faster** | ✅ identical |
| Engine/page warm-reuse | 258.8s | 0.56× (*slower*) | ❌ 6 wrong |

**Sharing the browser saved nothing** (143.7s vs a 145.0s per-spec-browser baseline) — launching
Chromium was never the cost; an NR spec spends its ~18s on SPA boot, HAR routing, game-data load and
UI actions. It is kept anyway because it makes isolation *structural*, which is what makes parallel
contexts safe and what killed the leftover-list corruption for good.

So the engines split cleanly by where their cost lives:

- **BattleScribe UI** — expensive cold start (JVM + JavaFX), cannot parallelize (`MaxParallel = 1`)
  → **warm-reuse** wins (1.79–2.20×).
- **NewRecruit** — cheap cold start, parallelizes freely → **parallelism** wins (3.8× at 4 workers),
  and warm-reuse is worthless.

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

### NR-UI roster warm-reuse — still broken, re-measured 2026-07-31

Warm-reuse of the NR roster engine corrupts results: only the first roster-creating spec of a batch
passes. This section used to call that historical and assert **"this class of bug is now
structurally impossible — each spec gets its own `BrowserContext`, so there is no shared state to
leak."** That claim was wrong, and re-measurement falsified it. Per-spec `BrowserContext` isolation
is a property of the **pool**; host warm-reuse keeps *one* engine — and therefore one context, one
page, one set of Pinia stores — alive across specs, which is the whole point of it. Warm-reuse is
exactly the configuration the isolation argument does not cover.

Re-measured after #336 fixed `Cleanup`'s list deletion (it had been calling a `deleteList` action
the store does not have, so it deleted nothing), because the reuse-policy table recorded the
leftover row as the cause and that cause was now gone:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine newrecruit-ui --roster --filter "auto-select/,category/" \
  --policy-a "workers=1,reuse=on" --policy-b "workers=1,reuse=off"
```

**7 of 8 specs diverge** (`A=failed B=passed`); only the first passes. `compare` refuses to report
timing, which is correct — there is no speedup to quote for a configuration that changes verdicts.
The lever was connected: arm A cold-starts **1** engine, arm B cold-starts **8**.

**The recorded diagnosis was wrong, and fixing the row leak did not fix this.** Every failure is the
first `addForce` timing out, and Playwright's call log says the Create List faction control is not
*ambiguous* — the spec's catalogue is **absent from it**:

```
waiting for Locator(".box").First.Locator("select").First
  - locator resolved to <select>…</select>
- attempting select option action
  - did not find some options
```

**The rows really are gone.** Probing the store at the moment the dialog is open shows
`listData: []`. #336 works. The blocker is elsewhere, and wrapping `systemsStore.selectSystem` to log
its caller — the trick #334 used — names it exactly:

```
fn: selectSystem   args: ["auto-select-field-forces-skipped"]   ← the PREVIOUS spec's system
before: "auto-select-multiple-min"                              ← correct, set during this spec's setup
stack:  at Proxy.selectList (MEhuWOrQ.js)     ← listsStore.selectList
        at Proxy.selectList (sB_XufNO.js)     ← NR's MyLists page component
```

NR's list page calls `listsStore.selectList(<a previous spec's row>)` on navigation, and `selectList`
re-selects that row's system. So the faction dropdown renders the *previous* spec's books while this
spec asks for its own catalogue by label — "did not find some options", and `addForce` burns its 30s.
Same clobber as #334, reached by a different route, and it survives the rows being deleted because the
row object handed to `selectList` does not come from `listData`.

**Two remedies were tried and measured; neither works.** Recorded so nobody spends the afternoon
again:

1. *Select the loaded system explicitly in setup* (`systemsStore.selectSystem(systemId)` after
   `LoadGameDataAsync`, which otherwise never selects anything and relies on NR auto-selecting the
   only system a cold browser has). Verified to take effect — and then clobbered by the call above
   before the dialog opens. Note `selectSystem` takes an **id** and compares with `==`; passing the
   system object makes every comparison fail and the call a silent no-op returning null.
2. *Release NR's cached list reference at reset* — null `lastSelectedListKey` and invoke
   `listsStore.unloadList()`, the slot NR's editor page fills with `() => { this.list = null }` and
   which NR itself calls from `syncAllLists` when a list disappears. No change.

So the open question is narrow and specific: **where does the row object passed to `selectList` come
from, given `listData` is empty and neither of the above releases it?** Answer that and warm-reuse
correctness is probably one line away.

**NR warm-reuse stays disabled, and the reason it stays disabled is economics, not this bug.** Even
working, it buys NR nothing: the gamedata domain measured 0.92×. Parallelism is the lever (3.8× at 4
workers), and the policy already picks the worker count. Anyone tempted to revisit this should fix
the economics case first; the correctness bug is downstream of a change nobody has a reason to make.

CI never caught the original bug because the NR-UI roster lane runs a **single** spec.

## Reproducing

The ablation lever is `bs-spec compare --policy-a/--policy-b`: each arm gets its own
`ConcurrencyPlan` override, in the same `--policy` vocabulary `run` and `serve` use (`workers=N`,
`reuse=on|off`, `reuse-roster=on|off`, `reuse-gamedata=on|off`). `compare` runs both arms, asserts
per-spec verdict-equality, and only then reports timing.

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --policy-a "reuse=on" --policy-b "reuse=off"
```

Verified 2026-07-13 (54 executed specs, 113 reported): **verdicts identical, 2.21×** — reproducing
the recorded 2.20× figure through the new channel.

**A ratio alone is not evidence.** The two arms must be shown to have genuinely done different
things, or the lever is disconnected and the number means nothing. `compare` prints a per-arm trace
summary for exactly this; the load-bearing line is `engine starts`:

| | Arm A (`reuse=on`) | Arm B (`reuse=off`) |
|---|---|---|
| wall | 160.5s | 354.6s |
| engine starts | **1 cold, 53 reused** | **54 cold, 0 reused** |
| p50 spec | 2998.7ms | 6614.6ms |
| peak live `jvm` | 1 | 1 |

Arm A cold-starts the JVM once and reuses it for the other 53 specs; arm B cold-starts a fresh one
for every spec. **Note `peak live jvm` is 1 in both arms and that is correct, not a bug:** it is a
*concurrency* measure, and `battlescribe-ui` declares `MaxParallel = 1`, so at most one JVM is ever
alive at a time in either arm. Arm B's 54 JVM launches are sequential — the cost shows up in the
`54 cold` count and the wall time, never as a higher peak. Read the cold/reuse counts, not the peak,
to confirm the arms differ.

`--config-a`/`--config-b` still exist and are a *different axis*: comma-separated `KEY=VALUE` child
**environment**, for genuine environment experiments. They are optional; varying the policy needs
only `--policy-a`/`--policy-b`.

### Historical note: `BSSPEC_DISABLE_WARM_REUSE`

The old lever was `--config-b "BSSPEC_DISABLE_WARM_REUSE=1"`. That variable was deleted when
warm-reuse moved to a `ConcurrencyPolicy` the parent computes and sends to the child. Because
`--config-*` is generic environment injection with no validation, the old recipe kept *running* — it
injected a variable nobody read, ran **both arms warm**, and reported "verdicts identical, 1.00×",
which reads exactly like confirmation. The lever was disconnected while the gauge said PASS. It is
recorded here because a false green is worse than a red one, and because `--config-*`'s
no-validation property still makes that failure mode possible for any other dead variable.

## Related

- **#303 (fixed)** — `AdapterProcess` used to buffer the engine host's stderr instead of forwarding
  it, so host-side diagnostics were invisible during a run (this actively obstructed the NR-UI
  roster diagnosis above). Fixed by commit `3a564a0` ("closes #303"): stderr lines are now also
  forwarded live to the parent's stderr, tagged with the worker index.
- **#304** — `SpecSuiteRunner` has no recovery when a pooled adapter process dies; one crash fails
  every remaining spec on that worker.
